using System.Collections.Concurrent;
using GameServer.Entities;
using GameServer.Items;
using ServerLib.Core;
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
/// <see cref="RaidRewardApplier.DrainAsync"/>(드레인 루프, 1개, 2026-07-09 SRP 분리로 이 클래스에서
/// 이관됨)는 grant를 해당 세션의
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
/// 이 토큰으로 <c>Register</c>하지 않으므로(오직 <see cref="PeriodicTimer.WaitForNextTickAsync(CancellationToken)"/>
/// 인자로만 전달) dispose 생략이 안전하다(사이클 1 <c>SessionBattleRunner</c>와 동일 근거, 2026-07-09
/// Task.Delay→PeriodicTimer 전환 이후에도 이 근거는 그대로 유지됨 — 둘 다 토큰을 인자로만 받아
/// await별로 임시 등록/해제할 뿐, 이 CTS에 영구 콜백을 남기지 않는다).</description></item>
/// <item><description><b>접속 콜백은 절대 전투 루프를 기다리면 안 된다:</b> <c>SocketPipelineListener.AcceptLoopAsync</c>가
/// 단일 accept 루프 안에서 <c>OnClientConnected</c>를 직접 await하므로, <see cref="OnConnected"/>는
/// 세션 제출 루프를 <c>Task.Run</c>으로 fire-and-forget 시작한다(사이클 1과 동일 근거).</description></item>
/// <item><description><b>브로드캐스트는 액터 루프와 완전히 분리되어 있다(코드리뷰 HIGH 발견 수정,
/// 2026-07-08):</b> <see cref="RaidEncounter.RunAsync"/>의 <c>onStep</c> 콜백(<see cref="RaidBroadcaster.OnStepAsync"/>)은
/// 내부 채널에 <c>TryWrite</c>만 하고 즉시 반환한다 — 실제 <see cref="ISessionRegistry.BroadcastAsync"/>
/// 네트워크 전송은 <see cref="RaidBroadcaster"/>의 별도 드레인 태스크가 전담한다. 이전에는
/// <c>onStep</c>이 브로드캐스트를 직접 동기 await해, 수신 버퍼를 비우지 않는 정지된 클라이언트 1명이
/// <c>SessionSendTimeout</c> 미설정 시 레이드 액터 전체를 무한 정지시키고 무경계 <c>_damageChannel</c>이
/// 무한 증가하는 OOM 경로가 있었다(<c>docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md</c>
/// HIGH 발견). 지금은 브로드캐스트가 아무리 느리거나 영원히 끝나지 않아도 액터의 피해 처리·기여도
/// 집계·데드라인 판정은 전혀 영향받지 않는다.</description></item>
/// <item><description><b>SRP 분리(코드리뷰 Medium 발견 수정, 2026-07-09):</b> 브로드캐스트 직렬화/
/// 스로틀/전송 책임은 <see cref="RaidBroadcaster"/>로, 보상 라우팅 책임은 <see cref="RaidRewardApplier"/>로
/// 분리했다. 이 클래스는 세션 생명주기(접속/해제 등록)와 세션별 제출 루프 오케스트레이션만
/// 담당한다.</description></item>
/// </list>
/// </remarks>
public sealed class SessionRaidRunner
{
    private const int BossMonsterId = 7001;

    /// <summary><see cref="SubmitLoopAsync"/>가 <c>tickInterval</c>을 지정하지 않으면 사용하는 기본 간격.</summary>
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMilliseconds(500);

    private readonly PlayerLevelSystem _levelSystem;
    private readonly EquipmentTable _equipmentTable;
    private readonly GameEventSink _sink;
    private readonly TimeSpan? _tickInterval;
    private readonly Monster _boss;
    private readonly RaidEncounter _raid;
    private readonly RaidBroadcaster _broadcaster;
    private readonly RaidRewardApplier _rewardApplier;

    // ConcurrentDictionary<Guid, ...>: 세션 접속/해제가 여러 I/O 스레드에서 동시에 일어나므로 락 없는
    // 딕셔너리로 세션별 컨텍스트를 관리한다(사이클 1 SessionBattleRunner와 동일 근거).
    private readonly ConcurrentDictionary<Guid, SessionRaidContext> _bySessionId = new();

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
        _tickInterval = tickInterval;
        _broadcaster = new RaidBroadcaster(registry);
        _rewardApplier = new RaidRewardApplier();

        // _boss를 별도 필드로 보관 — RaidEncounter는 boss 참조를 캡슐화(private)하므로, 세션 제출
        // 루프가 CalcFinalDamage(player, boss)로 피해를 계산하려면 이 클래스가 같은 인스턴스를
        // 직접 들고 있어야 한다(보스는 생성 후 불변이라 여러 스레드가 동시에 읽어도 안전).
        _boss = MonsterFactory.Create(monsterTable.GetById(BossMonsterId));
        _raid = new RaidEncounter(_boss, raidTimeLimit);
    }

    /// <summary>레이드 액터 루프·보상 드레인 루프·브로드캐스트 드레인 루프를 서버 수명 동안 1회 시작한다.</summary>
    /// <param name="lifetimeToken">서버 종료 시 취소되는 수명 토큰(세션별 CTS와는 무관 — 링크하지 않음)</param>
    /// <remarks>
    /// <b>Blocking 여부:</b> Non-blocking. 세 루프 모두 <c>Task.Run</c>으로 fire-and-forget 시작한다 —
    /// 호출자(<c>Main.cs</c>)는 이 메서드 반환 직후 <c>listener.Start</c>로 진행할 수 있다.
    /// </remarks>
    public void Start(CancellationToken lifetimeToken)
    {
        _ = Task.Run(() => _raid.RunAsync(_sink, lifetimeToken, _broadcaster.OnStepAsync), lifetimeToken);
        _ = Task.Run(() => _rewardApplier.DrainAsync(_raid.RewardReader, lifetimeToken), lifetimeToken);
        _ = Task.Run(() => _broadcaster.DrainAsync(lifetimeToken), lifetimeToken);
    }

    /// <summary>접속 시 시작 장비를 착용시키고 이 세션의 보스 공격 제출 루프를 시작한다.</summary>
    /// <param name="session">방금 연결된 세션. <see cref="ISession.Context"/>에 <see cref="Player"/>가
    /// 이미 부착돼 있어야 한다(<c>SessionPlayerBinder.OnConnected</c>가 이 메서드보다 먼저 실행돼야
    /// 함 — 배선 순서는 <c>Main.cs</c>의 <c>OnClientConnected</c> 참고).</param>
    /// <remarks>
    /// <b>Thread Context:</b> <c>SocketPipelineListener.AcceptLoopAsync</c>의 단일 accept 루프에서
    /// 직접 await됩니다. <b>Blocking 여부:</b> Non-blocking — 제출 루프는 <c>Task.Run</c>으로
    /// fire-and-forget 시작하므로 이 메서드 자체는 즉시 반환합니다(accept 루프를 절대 블로킹하지
    /// 않음, 클래스 remarks 참고). <b>Thread Safety:</b> Thread-safe — 세션별 컨텍스트는
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/>(<see cref="_bySessionId"/>)로 관리되고, 보상
    /// 라우팅용 InstanceId 인덱스는 <see cref="RaidRewardApplier"/>가 별도로 소유하므로(2026-07-09
    /// SRP 분리) 여러 세션이 동시에 접속해도 락 경합 없이 안전하다. <see cref="Player"/> 컨텍스트가
    /// 없거나(<c>TryGetContext</c> 실패) 동일 <c>SessionId</c>가 이미 등록돼 있으면(<c>TryAdd</c> 실패)
    /// no-op으로 조용히 반환한다 — 두 경우 모두 <c>SessionPlayerBinder</c>/리스너가 이미 세션 생명주기를
    /// 보장하는 상황에서만 발생 가능한 방어적 분기다.
    /// </remarks>
    public ValueTask OnConnected(ISession session)
    {
        if (!session.TryGetContext<Player>(out var player))
        {
            return ValueTask.CompletedTask;
        }

        StarterGearEquipper.Equip(player, _equipmentTable);

        var ctx = new SessionRaidContext { Player = player, Cts = new CancellationTokenSource() };
        if (!_bySessionId.TryAdd(session.SessionId, ctx))
        {
            ctx.Cts.Dispose(); // TryAdd 실패(중복 SessionId) — 아직 아무도 안 쓴 CTS라 즉시 dispose 안전
            return ValueTask.CompletedTask;
        }
        _rewardApplier.Register(player.InstanceId, ctx.PendingRewards);

        // Task.Run(fire-and-forget): accept 루프 블로킹 금지(위 클래스 remarks 참고).
        _ = Task.Run(() => SubmitLoopAsync(ctx));

        return ValueTask.CompletedTask;
    }

    /// <summary>해제 시 이 세션의 제출 루프를 취소하고 두 딕셔너리에서 제거한다.</summary>
    /// <param name="session">방금 연결이 끊어진 세션. <see cref="OnConnected"/>에서 이미 등록됐던
    /// 세션이 아니면(예: <c>TryAdd</c> 실패로 애초에 등록되지 않았던 세션) no-op이다.</param>
    /// <remarks>
    /// <b>Thread Context:</b> 리스너의 수신 루프 <c>finally</c>에서 비동기 발화됩니다(연결별로 다른
    /// I/O 스레드일 수 있음). <b>Blocking 여부:</b> Non-blocking — <c>Cts.Cancel()</c>은 등록된
    /// 콜백을 동기 실행하지만 이 세션의 <see cref="SubmitLoopAsync"/> 루프는 그 콜백을 등록하지
    /// 않으므로(클래스 remarks의 "세션 CTS는 링크하지 않는다" 참고) 즉시 반환한다. <b>Thread Safety:</b>
    /// Thread-safe — <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove(TKey,out TValue)"/>로
    /// 딕셔너리 갱신과 제출 루프 취소를 원자적으로 수행한다.
    /// </remarks>
    public ValueTask OnDisconnected(ISession session)
    {
        if (_bySessionId.TryRemove(session.SessionId, out var ctx))
        {
            ctx.Cts.Cancel(); // Dispose는 하지 않는다 — 위 클래스 remarks의 "세션 CTS는 링크하지 않는다" 참고
            _rewardApplier.Unregister(ctx.Player.InstanceId);
        }

        return ValueTask.CompletedTask;
    }


    /// <summary>
    /// 이 세션이 보스를 공격하는 반복 루프. <see cref="Entity.Update"/>(버프 틱·자연 회복)는 호출하지
    /// 않는다 — 보스가 반격하지 않아(Atk=0) 버프/회복이 전투 결과에 영향을 주지 않고, FinalStats는
    /// 접속 시 장비 착용과 처치 시 레벨업에서만 갱신되면 충분하다(설계 결정, 향후 버프 도입 시 재검토).
    /// </summary>
    /// <remarks>
    /// <b>코드리뷰 Medium 발견 수정(성능):</b> <c>Task.Delay(interval, token)</c>를 루프마다 새로
    /// 호출하면 매 틱 새 타이머 등록/해제 오버헤드가 발생한다. <see cref="PeriodicTimer"/>는 틱마다
    /// 재사용되는 단일 타이머 핸들이라 이 오버헤드가 없다 — 접속 세션 수만큼 병렬로 도는 이 루프의
    /// 틱 빈도(기본 500ms)를 고려하면 세션이 많아질수록 절감이 커진다.
    /// </remarks>
    private async Task SubmitLoopAsync(SessionRaidContext ctx)
    {
        var interval = _tickInterval ?? DefaultTickInterval;
        // PeriodicTimer: 내부적으로 단일 OS 타이머 핸들을 재사용해 WaitForNextTickAsync를 반복
        // 호출해도 매번 새 타이머를 등록/해제하지 않는다(Task.Delay 대비 틱당 할당·타이머 큐 조작 감소).
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (true)
            {
                // 드레인 루프가 큐에 넣어둔 보상을 이 세션(=이 Player의 유일한 소유 스레드)에서만 적용.
                RaidRewardApplier.ApplyPending(ctx.Player, ctx.PendingRewards, _levelSystem);

                var damage = BattleManager.Instance.CalcFinalDamage(ctx.Player, _boss);
                _raid.SubmitDamage(ctx.Player.InstanceId, damage);

                await timer.WaitForNextTickAsync(ctx.Cts.Token); // 취소 시 즉시 OperationCanceledException으로 깨어남
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

}
