using System.Collections.Concurrent;
using GameServer.Entities;
using GameServer.Items;
using ServerLib.Core;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace GameServer.Systems;

/// <summary>
/// 소켓 연결마다 독립적인 몬스터를 자동으로(방치형 스타일) 사냥시키고, 매 틱 결과를
/// <see cref="MobHpPacket"/>/<see cref="MobDeathPacket"/>으로 그 세션에만 전송하는 클래스입니다.
/// <see cref="SessionPlayerBinder"/>와 나란히 <see cref="ISession"/>을 다루는 두 번째(이자 마지막)
/// GameServer 타입입니다.
/// </summary>
/// <remarks>
/// <b>[사용 순서]</b> <see cref="OnConnected"/>는 <see cref="SessionPlayerBinder.OnConnected"/>가
/// 먼저 실행되어 <c>session.Context</c>에 <see cref="Player"/>를 부착한 뒤에 호출되어야 합니다
/// (Main.cs가 이 순서로 콜백을 조합합니다). <see cref="OnDisconnected"/>는 반대로 먼저 실행되어
/// 전투 루프를 정지시킨 뒤 <see cref="SessionPlayerBinder.OnDisconnected"/>가 연결 해제를 기록하게
/// 합니다.
/// <br/><br/>
/// <b>[Thread Safety:]</b> Thread-safe. 여러 세션이 동시에 연결·해제되어도 안전합니다 — 세션별
/// 가변 상태(몬스터·취소 토큰·세대 번호)는 <see cref="_battles"/>(스레드 안전한
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>)에만 저장하고, 그 외에는 읽기 전용으로 주입된
/// <see cref="BattleLoop"/>/<see cref="MonsterTable"/>/<see cref="EquipmentTable"/>/<see cref="GameEventSink"/>만
/// 참조합니다. 서로 다른 세션의 <see cref="Player"/>/<see cref="Monster"/> 인스턴스는 절대 공유되지
/// 않으므로(세션마다 새로 생성), <see cref="BattleLoop.RunAsync"/>를 세션 수만큼 동시에 돌리는 것이
/// 바로 이 클래스가 의도하는 시나리오입니다(예전 스레드 샤딩 데모와 동일한 동시성 전제 —
/// <see cref="BattleManager.Instance"/>가 <c>Random.Shared</c>로 이미 이를 지원).
/// <br/><br/>
/// <b>왜 별도의 <see cref="ConcurrentDictionary{TKey,TValue}"/>를 쓰는가:</b> <c>session.Context</c>는
/// 단일 객체 슬롯이며 이미 <see cref="SessionPlayerBinder"/>가 <see cref="Player"/>로 점유하고
/// 있습니다. 이 클래스가 필요로 하는 몬스터·취소 토큰·세대 번호는 그 슬롯에 함께 담을 수 없으므로
/// (덮어쓰면 <c>SessionPlayerBinder</c>의 계약이 깨짐), <c>SessionId</c>를 키로 하는 별도의 맵을
/// 이 클래스가 전용으로 소유합니다.
/// <br/><br/>
/// <b>[Memory Allocation:]</b> 연결마다 <see cref="Monster"/> 1개, 장비 3개(무기·방어구·장신구),
/// <see cref="CancellationTokenSource"/> 1개, <see cref="SessionBattleContext"/> 1개, 백그라운드
/// <see cref="Task"/> 1개를 할당합니다.
/// <br/><br/>
/// <b>[Blocking 여부:]</b> <see cref="OnConnected"/>/<see cref="OnDisconnected"/> 모두 즉시 반환
/// (Non-blocking) — 전투 루프 자체는 <c>Task.Run</c>으로 떼어낸 백그라운드 작업이라 accept 루프를
/// 막지 않습니다(아래 <see cref="OnConnected"/> 문서 참고).
/// </remarks>
public sealed class SessionBattleRunner
{
    private const int StarterMonsterId = 2003;    // 고블린 — 스테이지/스포너 시스템 도입 전까지 고정 시작 몬스터
    private const int StarterWeaponId = 4001;     // 낡은 검
    private const int StarterArmorId = 5001;      // 가죽 갑옷
    private const int StarterAccessoryId = 6001;  // 낡은 반지

    private readonly BattleLoop _loop;
    private readonly MonsterTable _monsterTable;
    private readonly EquipmentTable _equipmentTable;
    private readonly GameEventSink _sink;
    private readonly TimeSpan? _tickInterval;

    // ConcurrentDictionary: 여러 I/O 스레드가 동시에 서로 다른 세션을 연결·해제할 수 있어
    // 락 없는 스레드 안전 삽입/제거가 필요하다. SessionId(Guid)를 키로 세션별 전투 상태를 보관한다
    // (session.Context는 이미 Player로 점유되어 있어 이 용도로 재사용할 수 없다 — 위 클래스 문서 참고).
    private readonly ConcurrentDictionary<Guid, SessionBattleContext> _battles = new();

    /// <summary>세션 하나의 진행 중인 전투가 필요로 하는 가변 상태.</summary>
    private sealed class SessionBattleContext
    {
        public required Monster Monster { get; init; }

        // CancellationTokenSource: 이 세션의 전투 루프(Task.Run으로 분리된 백그라운드 작업) 전용
        // 취소 신호. 연결 해제 또는 송신 실패 시 이 토큰만 취소하면 해당 세션의 루프만 멈춘다 —
        // 다른 세션에는 영향이 없다(세션마다 독립 인스턴스).
        public required CancellationTokenSource Cts { get; init; }

        public int Generation { get; set; } = 1;
    }

    /// <summary>지정한 시스템·테이블·싱크로 러너를 생성합니다.</summary>
    /// <param name="levelSystem">전투 루프가 사용할 레벨 시스템</param>
    /// <param name="monsterTable">시작 몬스터를 조회할 테이블</param>
    /// <param name="equipmentTable">시작 장비를 조회할 테이블</param>
    /// <param name="sink">전투 이벤트(몬스터 처치 등)를 기록할 싱크</param>
    /// <param name="tickInterval">틱 간격. 생략 시 <see cref="BattleLoop.RunAsync"/>의 기본값(500ms) 사용.</param>
    public SessionBattleRunner(PlayerLevelSystem levelSystem, MonsterTable monsterTable,
        EquipmentTable equipmentTable, GameEventSink sink, TimeSpan? tickInterval = null)
    {
        ArgumentNullException.ThrowIfNull(levelSystem);
        ArgumentNullException.ThrowIfNull(monsterTable);
        ArgumentNullException.ThrowIfNull(equipmentTable);
        ArgumentNullException.ThrowIfNull(sink);

        _loop = new BattleLoop(levelSystem);
        _monsterTable = monsterTable;
        _equipmentTable = equipmentTable;
        _sink = sink;
        _tickInterval = tickInterval;
    }

    /// <summary>
    /// 연결 직후 호출됩니다. <c>session.Context</c>에 이미 부착된 <see cref="Player"/>에게 시작
    /// 장비를 채워주고, 시작 몬스터를 스폰해 전투 루프를 백그라운드로 시작합니다.
    /// </summary>
    /// <param name="session">새로 연결된 세션</param>
    /// <returns>완료된(무할당) <see cref="ValueTask"/></returns>
    /// <remarks>
    /// <b>왜 전투 루프를 여기서 기다리지 않는가:</b> ServerLib 소스 확인 결과
    /// (<c>SocketPipelineListener.AcceptLoopAsync</c>), <c>OnClientConnected</c>는 서버의 단일 accept
    /// 루프 안에서 직접 호출됩니다. 여기서 사실상 무한 루프인 전투를 <c>await</c>하면 그 순간부터
    /// 서버는 다른 어떤 클라이언트의 연결도 받아들이지 못합니다. 그래서 <c>_ = Task.Run(...)</c>으로
    /// 완전히 떼어낸 뒤 즉시 반환합니다(예전 Main.cs가 레이드 액터를 <c>Task.Run(() =&gt;
    /// raid.RunAsync(...))</c>으로 띄우던 것과 동일한 이유).
    /// </remarks>
    public ValueTask OnConnected(ISession session)
    {
        if (!session.TryGetContext<Player>(out var player))
        {
            // SessionPlayerBinder.OnConnected가 먼저 실행되지 않은 이례적인 배선 오류 — 전투를
            // 시작할 대상이 없으므로 조용히 건너뛴다(예외로 accept 루프를 방해하지 않는다).
            return ValueTask.CompletedTask;
        }

        EquipStarterGear(player);

        var monster = MonsterFactory.Create(_monsterTable.GetById(StarterMonsterId));
        var ctx = new SessionBattleContext { Monster = monster, Cts = new CancellationTokenSource() };

        if (!_battles.TryAdd(session.SessionId, ctx))
        {
            // 동일 SessionId가 이미 등록돼 있는 이례적 상황 — 이 Cts는 어떤 루프에도 전달되지
            // 않았으므로(RunSessionBattleAsync를 아직 시작하지 않음) 여기서 바로 Dispose해도 안전하다.
            ctx.Cts.Dispose();
            return ValueTask.CompletedTask;
        }

        // Task.Run: 스레드 풀에서 전투 루프를 구동한다. 위 문서 참고 — accept 루프를 막지 않기 위해
        // 반드시 fire-and-forget이어야 한다. 예외는 RunSessionBattleAsync 내부에서 전부 처리하므로
        // 여기서는 반환된 Task를 관찰하지 않는다(미관측 예외가 될 위험 없음).
        _ = Task.Run(() => RunSessionBattleAsync(session, player, ctx));

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 연결 해제 직후 호출됩니다. 해당 세션의 전투 루프를 취소합니다.
    /// </summary>
    /// <param name="session">해제된 세션</param>
    /// <returns>완료된(무할당) <see cref="ValueTask"/></returns>
    /// <remarks>
    /// 딕셔너리에서의 제거는 이 메서드가 아니라 <see cref="RunSessionBattleAsync"/>의
    /// <c>finally</c>가 담당한다(루프 자신이 정리). 이 메서드는 <c>Cancel()</c>만 호출한다 — 전투
    /// 루프는 이 토큰을 <c>Register</c>하거나 <c>Task.Delay</c>에 전달하지 않으므로 <c>Dispose</c>되지
    /// 않은 채로 안전하게 취소만 반복 호출될 수 있다(취소 후 재취소도 안전).
    /// </remarks>
    public ValueTask OnDisconnected(ISession session)
    {
        if (_battles.TryGetValue(session.SessionId, out var ctx))
        {
            ctx.Cts.Cancel();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>예전(제거된) Main.cs와 동일한 고정 시작 장비를 착용시킨다.</summary>
    private void EquipStarterGear(Player player)
    {
        player.Equipment.Equip(EquipmentFactory.Create(_equipmentTable.GetById(StarterWeaponId)), SlotType.Weapon);
        player.Equipment.Equip(EquipmentFactory.Create(_equipmentTable.GetById(StarterArmorId)), SlotType.Armor);
        player.Equipment.Equip(EquipmentFactory.Create(_equipmentTable.GetById(StarterAccessoryId)), SlotType.Accessory);
        player.UpdateFinalStats();
        player.RestoreResources();
    }

    /// <summary>
    /// 한 세션의 전투 루프 본체. <see cref="Task.Run(Action)"/>으로 별도 실행되며,
    /// <see cref="BattleLoop.RunAsync"/>의 <c>onTick</c> 콜백으로 <see cref="OnTickAsync"/>를 주입한다.
    /// </summary>
    /// <remarks>
    /// 이 세션의 실패가 다른 세션에 전파되지 않도록 예외를 전부 이 메서드 안에서 격리한다
    /// (<c>ShardBattleRunner</c>/<c>RaidEncounter</c>와 동일한 원칙). 취소는 정상 종료로 취급하고,
    /// 그 외 예외는 <see cref="GameEventSink.RecordPlayerConnectionError"/>로 기록한다.
    /// <br/><br/>
    /// <b>왜 <c>finally</c>에서 <c>ctx.Cts.Dispose()</c>를 호출하지 않는가:</b> 이 세션의 CTS는
    /// <see cref="OnDisconnected"/>(소켓 종료)와 <see cref="OnTickAsync"/>의 송신 실패 자가취소, 두
    /// 경로에서 취소될 수 있다. 만약 여기서 CTS를 Dispose한 직후 다른 스레드의 <c>OnDisconnected</c>가
    /// (아직 살아있는 줄 알고) <c>Cancel()</c>을 호출하면 <see cref="ObjectDisposedException"/>이
    /// 발생해 그 호출을 감싸는 <c>Main.cs</c>의 연결 해제 조합 콜백이 깨지고, 뒤이어 실행되어야 할
    /// <c>SessionPlayerBinder.OnDisconnected</c>(연결 해제 기록)까지 실행되지 못한다.
    /// <see cref="BattleLoop.RunAsync"/>는 이 토큰으로 <c>Register</c>하거나 <c>Task.Delay</c>에
    /// 전달하지 않으므로(내부적으로 <c>WaitHandle</c>을 할당하지 않음) Dispose를 생략해도 리소스
    /// 누수는 없다 — GC가 안전하게 회수한다.
    /// </remarks>
    private async Task RunSessionBattleAsync(ISession session, Player player, SessionBattleContext ctx)
    {
        try
        {
            await _loop.RunAsync(player, ctx.Monster, _tickInterval, ctx.Cts.Token, _sink,
                (evt, p, m, ct) => OnTickAsync(session, ctx, evt, p, m, ct));
        }
        catch (OperationCanceledException)
        {
            // 정상 종료 경로(연결 해제 또는 송신 실패로 인한 자가 취소) — 별도 처리 불필요.
        }
        catch (Exception ex)
        {
            _sink.RecordPlayerConnectionError(player.InstanceId, ex);
        }
        finally
        {
            _battles.TryRemove(session.SessionId, out _); // 주의: ctx.Cts.Dispose() 호출하지 않음 — 위 remarks 참고
        }
    }

    /// <summary>
    /// <see cref="BattleLoop.RunAsync"/>의 <c>onTick</c> 콜백. 이번 틱 결과를 패킷으로 변환해 그
    /// 세션에만 전송한다.
    /// </summary>
    /// <remarks>
    /// <c>session.SendAsync</c>가 예외(연결이 이미 끊긴 경우의 <see cref="ObjectDisposedException"/>/
    /// <see cref="System.Net.Sockets.SocketException"/> 등)를 던지면 이 세션의 CTS만 취소하고
    /// 삼킨다 — 죽은 연결에 계속 전투를 진행시키지 않되, 다른 세션에는 영향을 주지 않는다.
    /// </remarks>
    private async ValueTask OnTickAsync(ISession session, SessionBattleContext ctx, BattleTickEvent evt,
        Player player, Monster monster, CancellationToken cancellationToken)
    {
        var set = SessionBattlePackets.BuildTickPackets(evt, player, monster, ctx.Generation);
        ctx.Generation = set.NextGeneration;

        try
        {
            if (set.Death is not null)
            {
                await session.SendAsync(set.Death, cancellationToken);
            }

            await session.SendAsync(set.Hp, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Cts.Cancel();
        }
    }
}
