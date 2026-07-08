using System.Buffers;
using System.Collections.Concurrent;
using GameServer.Entities;
using GameServer.Items;
using ServerLib.Core;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace GameServer.Systems;

/// <summary>
/// 접속한 모든 세션이 하나의 공유 레이드 보스(몬스터 7001)를 동시에 공격하는 co-op 전투를 배선한다.
/// <see cref="SessionPlayerBinder"/>와 나란히 <see cref="ISession"/>을 다루는 두 번째(이자 마지막)
/// GameServer 네트워크 계층 클래스다. 사이클 1의 <c>SessionBattleRunner</c>(세션별 독립 몬스터)를
/// 대체한다 — 그 클래스와 테스트는 git 이력에 보존되며 이 서버 경로에서는 배선하지 않는다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>보스 소유권:</b> 공유 보스(<see cref="Monster"/>)는 <see cref="RaidEncounter"/>의
/// 액터 루프(<see cref="RaidEncounter.RunAsync"/>)만이 HP를 변경한다. 이 클래스가 시작하는 세션별
/// 제출 루프(<see cref="SubmitLoopAsync"/>)는 보스의 불변 필드(Def/ArmorPen)만 읽어
/// <see cref="BattleManager.CalcFinalDamage"/>로 피해 숫자를 계산하고 <see cref="RaidEncounter.SubmitDamage"/>로
/// 보낼 뿐, 보스 HP를 절대 직접 건드리지 않는다.</description></item>
/// <item><description><b>보상 단일 소유 원칙:</b> <see cref="RaidEncounter.RewardReader"/>를 소비하는
/// <see cref="DrainRewardsAsync"/>(드레인 루프, 1개)는 grant를 해당 세션의
/// <see cref="ConcurrentQueue{T}"/>에 enqueue만 할 뿐 <see cref="Player"/>를 절대 직접 만지지 않는다.
/// <see cref="Player.AddExp"/>/<see cref="Player.AddGold"/>/레벨업 판정은 오직 그 Player를 소유한
/// <see cref="SubmitLoopAsync"/>만 수행한다 — 서로 다른 스레드가 동시에 같은 Player의
/// <c>UpdateFinalStats</c>를 재계산하는 레이스를 막기 위함이다.</description></item>
/// <item><description><b>세션 CTS는 링크하지 않는다:</b> <see cref="OnConnected"/>에서 만드는
/// <see cref="CancellationTokenSource"/>는 서버 수명 토큰에 <c>CreateLinkedTokenSource</c>로 연결하지
/// 않는다 — 링크하면 부모 토큰에 콜백이 등록되는데, 수명 토큰은 서버 전체 생애 동안 살아있으므로
/// 접속했다 끊은 모든 세션의 등록이 <c>Dispose()</c> 전까지 누적된다(누수). 대신 <see cref="OnDisconnected"/>
/// 에서만 <c>Cancel()</c>하고(서버 종료 시 <c>listener.Stop()</c>이 모든 활성 세션의 해제 콜백을
/// 구동해 자연히 전파됨), 이 CTS는 <c>Dispose()</c>하지 않는다 — <see cref="SubmitLoopAsync"/>가
/// 이 토큰으로 <c>Register</c>하지 않으므로(오직 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
/// 인자로만 전달) dispose 생략이 안전하다(사이클 1 <c>SessionBattleRunner</c>와 동일 근거).</description></item>
/// <item><description><b>접속 콜백은 절대 전투 루프를 기다리면 안 된다:</b> <c>SocketPipelineListener.AcceptLoopAsync</c>가
/// 단일 accept 루프 안에서 <c>OnClientConnected</c>를 직접 await하므로, <see cref="OnConnected"/>는
/// 세션 제출 루프를 <c>Task.Run</c>으로 fire-and-forget 시작한다(사이클 1과 동일 근거).</description></item>
/// </list>
/// </remarks>
public sealed class SessionRaidRunner
{
    private const int BossMonsterId = 7001;
    private const int StarterWeaponId = 4001;
    private const int StarterArmorId = 5001;
    private const int StarterAccessoryId = 6001;

    /// <summary><see cref="SubmitLoopAsync"/>가 <c>tickInterval</c>을 지정하지 않으면 사용하는 기본 간격.</summary>
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMilliseconds(500);

    // HP 브로드캐스트 스로틀: BossDamaged/None 스텝은 이 간격보다 자주 전 세션에 보내지 않는다(N명
    // 접속 시 매 피해 제출마다 브로드캐스트하면 틱당 N×N 전송이 되는 것을 방지). 처치/실패는 항상
    // 즉시 보낸다(BroadcastStepAsync에서 이벤트 종류로 분기).
    private static readonly TimeSpan HpBroadcastThrottle = TimeSpan.FromMilliseconds(150);

    private readonly PlayerLevelSystem _levelSystem;
    private readonly EquipmentTable _equipmentTable;
    private readonly GameEventSink _sink;
    private readonly ISessionRegistry _registry;
    private readonly TimeSpan? _tickInterval;
    private readonly Monster _boss;
    private readonly RaidEncounter _raid;

    // BinaryPacketSerializer: 무상태(내부 가변 필드 없음)라 여러 세션 제출 루프/액터 콜백이 동시에
    // 호출해도 안전 — PacketSendExtensions와 동일하게 인스턴스 1개를 공유한다(직렬화기 재할당 방지).
    private readonly BinaryPacketSerializer _serializer = new();

    // ConcurrentDictionary<Guid, ...>: 세션 접속/해제가 여러 I/O 스레드에서 동시에 일어나므로 락 없는
    // 딕셔너리로 세션별 컨텍스트를 관리한다(사이클 1 SessionBattleRunner와 동일 근거).
    private readonly ConcurrentDictionary<Guid, SessionRaidContext> _bySessionId = new();

    // ConcurrentDictionary<string, ...>: 드레인 루프가 보상 grant의 PlayerInstanceId로 세션 컨텍스트를
    // 찾아야 하므로 InstanceId를 보조 키로 둔 두 번째 인덱스 — 세션이 하나뿐이라 SessionId 딕셔너리와
    // 항상 1:1로 동기화된다(OnConnected/OnDisconnected에서 두 딕셔너리를 함께 갱신).
    private readonly ConcurrentDictionary<string, SessionRaidContext> _byInstanceId = new();

    // DateTime(액터 스레드 전용 필드): _raid.RunAsync의 onStep 콜백(BroadcastStepAsync)은 액터의 단일
    // 스레드에서만 순차 호출되므로(RaidEncounter의 액터 불변식과 동일) 이 필드에 락이 필요 없다.
    private DateTime _lastHpBroadcastUtc = DateTime.MinValue;

    private sealed class SessionRaidContext
    {
        public required Player Player { get; init; }
        public required CancellationTokenSource Cts { get; init; }

        // ConcurrentQueue<RaidRewardGrant>: 드레인 루프(생산자)가 enqueue, 이 세션의 제출 루프(단일
        // 소비자)가 dequeue — 락-프리 SPSC 상황이지만 다중 생산자에도 안전한 lock-free 큐라 문제 없음.
        public readonly ConcurrentQueue<RaidRewardGrant> PendingRewards = new();
    }

    /// <summary>공유 레이드 보스 co-op 러너를 생성한다. 이 시점에 보스(몬스터 7001)를 1회 스폰한다.</summary>
    /// <param name="levelSystem">보상 적용 후 레벨업 판정에 사용할 시스템</param>
    /// <param name="monsterTable">보스 템플릿(7001) 조회용 몬스터 마스터 테이블</param>
    /// <param name="equipmentTable">접속 시 착용시킬 시작 장비(4001/5001/6001) 조회용 테이블</param>
    /// <param name="sink">전투/연결 이벤트를 기록할 싱크</param>
    /// <param name="registry">브로드캐스트 대상(접속 세션 전체)을 추적하는 레지스트리</param>
    /// <param name="raidTimeLimit">이 시간 내에 보스를 처치하지 못하면 레이드 실패(보상 없이 리셋)</param>
    /// <param name="tickInterval">세션별 제출 루프의 틱 간격. 생략 시 500ms.</param>
    public SessionRaidRunner(PlayerLevelSystem levelSystem, MonsterTable monsterTable, EquipmentTable equipmentTable,
        GameEventSink sink, ISessionRegistry registry, TimeSpan raidTimeLimit, TimeSpan? tickInterval = null)
    {
        ArgumentNullException.ThrowIfNull(levelSystem);
        ArgumentNullException.ThrowIfNull(monsterTable);
        ArgumentNullException.ThrowIfNull(equipmentTable);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(registry);

        _levelSystem = levelSystem;
        _equipmentTable = equipmentTable;
        _sink = sink;
        _registry = registry;
        _tickInterval = tickInterval;

        // _boss를 별도 필드로 보관 — RaidEncounter는 boss 참조를 캡슐화(private)하므로, 세션 제출
        // 루프가 CalcFinalDamage(player, boss)로 피해를 계산하려면 이 클래스가 같은 인스턴스를
        // 직접 들고 있어야 한다(보스는 생성 후 불변이라 여러 스레드가 동시에 읽어도 안전).
        _boss = MonsterFactory.Create(monsterTable.GetById(BossMonsterId));
        _raid = new RaidEncounter(_boss, raidTimeLimit);
    }

    /// <summary>레이드 액터 루프와 보상 드레인 루프를 서버 수명 동안 1회 시작한다.</summary>
    /// <param name="lifetimeToken">서버 종료 시 취소되는 수명 토큰(세션별 CTS와는 무관 — 링크하지 않음)</param>
    /// <remarks>
    /// <b>Blocking 여부:</b> Non-blocking. 두 루프 모두 <c>Task.Run</c>으로 fire-and-forget 시작한다 —
    /// 호출자(<c>Main.cs</c>)는 이 메서드 반환 직후 <c>listener.Start</c>로 진행할 수 있다.
    /// </remarks>
    public void Start(CancellationToken lifetimeToken)
    {
        _ = Task.Run(() => _raid.RunAsync(_sink, lifetimeToken, BroadcastStepAsync), lifetimeToken);
        _ = Task.Run(() => DrainRewardsAsync(lifetimeToken), lifetimeToken);
    }

    /// <summary>접속 시 시작 장비를 착용시키고 이 세션의 보스 공격 제출 루프를 시작한다.</summary>
    public ValueTask OnConnected(ISession session)
    {
        if (!session.TryGetContext<Player>(out var player))
        {
            return ValueTask.CompletedTask;
        }

        EquipStarterGear(player);

        var ctx = new SessionRaidContext { Player = player, Cts = new CancellationTokenSource() };
        if (!_bySessionId.TryAdd(session.SessionId, ctx))
        {
            ctx.Cts.Dispose(); // TryAdd 실패(중복 SessionId) — 아직 아무도 안 쓴 CTS라 즉시 dispose 안전
            return ValueTask.CompletedTask;
        }
        _byInstanceId[player.InstanceId] = ctx;

        // Task.Run(fire-and-forget): accept 루프 블로킹 금지(위 클래스 remarks 참고).
        _ = Task.Run(() => SubmitLoopAsync(ctx));

        return ValueTask.CompletedTask;
    }

    /// <summary>해제 시 이 세션의 제출 루프를 취소하고 두 딕셔너리에서 제거한다.</summary>
    public ValueTask OnDisconnected(ISession session)
    {
        if (_bySessionId.TryRemove(session.SessionId, out var ctx))
        {
            ctx.Cts.Cancel(); // Dispose는 하지 않는다 — 위 클래스 remarks의 "세션 CTS는 링크하지 않는다" 참고
            _byInstanceId.TryRemove(ctx.Player.InstanceId, out _);
        }

        return ValueTask.CompletedTask;
    }

    private void EquipStarterGear(Player player)
    {
        player.Equipment.Equip(EquipmentFactory.Create(_equipmentTable.GetById(StarterWeaponId)), SlotType.Weapon);
        player.Equipment.Equip(EquipmentFactory.Create(_equipmentTable.GetById(StarterArmorId)), SlotType.Armor);
        player.Equipment.Equip(EquipmentFactory.Create(_equipmentTable.GetById(StarterAccessoryId)), SlotType.Accessory);
        player.UpdateFinalStats();
        player.RestoreResources();
    }

    /// <summary>
    /// 이 세션이 보스를 공격하는 반복 루프. <see cref="Entity.Update"/>(버프 틱·자연 회복)는 호출하지
    /// 않는다 — 보스가 반격하지 않아(Atk=0) 버프/회복이 전투 결과에 영향을 주지 않고, FinalStats는
    /// 접속 시 장비 착용과 처치 시 레벨업에서만 갱신되면 충분하다(설계 결정, 향후 버프 도입 시 재검토).
    /// </summary>
    private async Task SubmitLoopAsync(SessionRaidContext ctx)
    {
        var interval = _tickInterval ?? DefaultTickInterval;
        try
        {
            while (true)
            {
                // 드레인 루프가 큐에 넣어둔 보상을 이 세션(=이 Player의 유일한 소유 스레드)에서만 적용.
                while (ctx.PendingRewards.TryDequeue(out var grant))
                {
                    ctx.Player.AddExp(grant.Exp);
                    ctx.Player.AddGold(grant.Gold);
                    _levelSystem.CheckLevelUp(ctx.Player);
                }

                var damage = BattleManager.Instance.CalcFinalDamage(ctx.Player, _boss);
                _raid.SubmitDamage(ctx.Player.InstanceId, damage);

                await Task.Delay(interval, ctx.Cts.Token); // 취소 시 즉시 OperationCanceledException으로 깨어남
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료(접속 해제) — 위 클래스 remarks 참고.
        }
        catch (Exception ex)
        {
            _sink.RecordPlayerConnectionError(ctx.Player.InstanceId, ex);
        }
    }

    /// <summary>보스 처치로 발생한 보상 grant를 InstanceId로 해당 세션 큐에 라우팅하는 단일 드레인 루프.</summary>
    /// <remarks>
    /// Player를 절대 직접 만지지 않는다(단일 소유 원칙, 클래스 remarks 참고) — 찾은 세션의
    /// <see cref="ConcurrentQueue{T}"/>에 enqueue만 한다. <see cref="_byInstanceId"/>에서 못 찾으면
    /// (이미 접속 종료한 임시 플레이어) grant를 드롭한다 — 영속화가 없는 임시 Player라 무해한 no-op이다.
    /// </remarks>
    private async Task DrainRewardsAsync(CancellationToken lifetimeToken)
    {
        try
        {
            await foreach (var grant in _raid.RewardReader.ReadAllAsync(lifetimeToken))
            {
                if (_byInstanceId.TryGetValue(grant.PlayerInstanceId, out var ctx))
                {
                    ctx.PendingRewards.Enqueue(grant);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료(서버 수명 토큰 취소).
        }
    }

    /// <summary>
    /// <see cref="RaidEncounter.RunAsync"/>의 onStep 콜백. HP 전용 스텝(None/BossDamaged)은
    /// <see cref="HpBroadcastThrottle"/> 간격으로 스로틀하고, 처치/실패는 항상 즉시 브로드캐스트한다.
    /// </summary>
    /// <remarks>액터의 단일 스레드에서만 호출되므로 <see cref="_lastHpBroadcastUtc"/> 갱신에 락이
    /// 필요 없다(클래스 remarks 참고).</remarks>
    private async ValueTask BroadcastStepAsync(RaidStepBroadcast info, CancellationToken cancellationToken)
    {
        bool isThrottledHpOnly = info.Event is RaidEventType.None or RaidEventType.BossDamaged;
        if (isThrottledHpOnly)
        {
            var now = DateTime.UtcNow;
            if (now - _lastHpBroadcastUtc < HpBroadcastThrottle)
            {
                return; // 스로틀 — 이번 HP 브로드캐스트는 건너뛴다(보스 판정 자체는 계속 진행됨)
            }
            _lastHpBroadcastUtc = now;
        }

        var (death, hp) = RaidBroadcastPackets.Build(info);
        if (death is not null)
        {
            await BroadcastPacketAsync(death, cancellationToken);
        }
        await BroadcastPacketAsync(hp, cancellationToken);
    }

    /// <summary>패킷 1개를 직렬화해 접속한 모든 세션에 브로드캐스트한다.</summary>
    /// <remarks>
    /// <b>Memory Allocation:</b> <see cref="ArrayPool{T}"/>.Shared에서 버퍼를 대여해 직렬화하고,
    /// <see cref="ISessionRegistry.BroadcastAsync"/>가 요구하는 대로 그 <c>ValueTask</c>가 완료될
    /// 때까지(모든 세션 전송 완료) 버퍼를 유효하게 유지한 뒤 <c>finally</c>에서 반납한다 —
    /// <see cref="PacketSendExtensions.SendAsync{T}(ISession, T, CancellationToken)"/>와 동일한 패턴.
    /// </remarks>
    private async ValueTask BroadcastPacketAsync<T>(T packet, CancellationToken cancellationToken) where T : IPacket
    {
        var buffer = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize + packet.GetBodySize());
        try
        {
            int written = _serializer.Serialize(packet, buffer);
            await _registry.BroadcastAsync(buffer.AsMemory(0, written), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
