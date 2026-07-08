using System.Diagnostics.Metrics;
using System.Net;
using GameServer.Items;
using GameServer.Stats;
using GameServer.Systems;
using ServerLib.Interface;

namespace GameServer.Tests.Systems;

/// <summary>
/// 코드리뷰 HIGH 발견(<c>docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md</c>) 회귀
/// 검증: 브로드캐스트가 영원히 끝나지 않아도(느린/정지된 클라이언트) 레이드 액터의 피해 처리가
/// 전혀 지연되지 않는지를 확인한다. 실 소켓을 쓰지 않고 <see cref="ISessionRegistry"/>를 가짜로
/// 대체해 "브로드캐스트가 결코 반환하지 않는" 최악의 경우를 직접 구성한다.
/// </summary>
public class SessionRaidRunnerBroadcastDecouplingTests
{
    /// <summary>OnConnected가 요구하는 최소 계약(Context에 Player)만 채운 최소 가짜 세션.</summary>
    private sealed class FakeSession : ISession
    {
        public Guid SessionId { get; } = Guid.NewGuid();
        public EndPoint? RemoteEndPoint => null;
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastReceivedAt => ConnectedAt;
        public DateTimeOffset LastProgressAt => ConnectedAt;
        public SessionState State { get; private set; } = SessionState.Connected;
        public bool TransitionTo(SessionState newState) { State = newState; return true; }
        public object? Context { get; set; }
        public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }
        public Func<ValueTask>? OnDisconnected { get; set; }
        public Func<Exception, ValueTask>? OnReceiveError { get; set; }
        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// <see cref="ISessionRegistry.BroadcastAsync"/>가 취소되기 전까지 절대 반환하지 않는 가짜
    /// 레지스트리 — "정지된 클라이언트로의 전송이 영원히 블록된다"는 최악의 시나리오를 그대로 구현한다.
    /// </summary>
    private sealed class NeverRespondingSessionRegistry : ISessionRegistry
    {
        public int Count => 0;
        public bool TryGet(Guid sessionId, out ISession? session) { session = null; return false; }
        public IReadOnlyCollection<ISession> GetAll() => Array.Empty<ISession>();
        public async ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
            => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    [Fact]
    public async Task NeverRespondingBroadcast_DoesNotBlockActorDamageProcessing()
    {
        // game.raid.boss_hp_percent 게이지는 RaidEncounter 액터 루프가 매 스텝(피해 처리) 끝에
        // 무조건 기록한다 — onStep(브로드캐스트) 완료 여부와 무관하게 액터가 실제로 전진하고
        // 있는지를 재는 직접적인 신호다. 다른 병렬 테스트와 겹치지 않도록 고유 Meter 이름 사용
        // (GameMetricsTests/GameEventSinkTests와 동일 관례).
        var meterName = $"Test.SessionRaidRunner.{Guid.NewGuid()}";
        var metrics = new GameMetrics(meterName);
        int hpGaugeFireCount = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "game.raid.boss_hp_percent" && instrument.Meter.Name == meterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => Interlocked.Increment(ref hpGaugeFireCount));
        listener.Start();

        await using var sink = new GameEventSink(TextWriter.Null, metrics);
        var levelSystem = PlayerLevelSystem.CreateDefault();
        var monsterTable = MonsterTable.CreateDefault(); // 보스(7001) Hp=5,000,000 — 이 테스트 창 안에 처치될 일 없음
        var equipmentTable = EquipmentTable.CreateDefault();
        var registry = new NeverRespondingSessionRegistry();

        var runner = new SessionRaidRunner(
            levelSystem, monsterTable, equipmentTable, sink, registry,
            raidTimeLimit: TimeSpan.FromMinutes(10), tickInterval: TimeSpan.FromMilliseconds(2)); // 빠른 틱 — 짧은 창에서 여러 번 관측

        using var lifetimeCts = new CancellationTokenSource();
        runner.Start(lifetimeCts.Token); // 액터 루프 + 보상 드레인 + 브로드캐스트 드레인 기동

        var session = new FakeSession { Context = PlayerFactory.CreateTemp(Guid.NewGuid(), levelSystem) };
        await runner.OnConnected(session); // 제출 루프 시작 — 즉시 SubmitDamage가 흐르기 시작

        // 브로드캐스트는 절대 안 끝나지만(NeverRespondingSessionRegistry), 액터는 이를 기다리지
        // 않아야 한다 — 300ms 동안 여러 번 게이지가 기록되면 액터가 계속 전진했다는 뜻이다.
        // (수정 전 코드였다면 onStep이 첫 호출에서 영원히 block되어 게이지가 단 한 번도 기록되지
        // 않는다 — 이 테스트가 그 회귀를 정확히 잡아낸다.)
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        lifetimeCts.Cancel();

        Assert.True(hpGaugeFireCount > 5,
            $"기대: 액터가 브로드캐스트를 기다리지 않고 여러 틱을 처리(게이지 5회 초과 기록). 실제: {hpGaugeFireCount}회 — " +
            "onStep이 아직 registry.BroadcastAsync를 동기 대기하고 있다면 이 값은 0에 머문다.");
    }
}
