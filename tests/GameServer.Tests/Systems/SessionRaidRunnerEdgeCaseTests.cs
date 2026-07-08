using System.Net;
using GameServer.Items;
using GameServer.Stats;
using GameServer.Systems;
using ServerLib.Interface;

namespace GameServer.Tests.Systems;

/// <summary>
/// 코드리뷰 스타일 Medium 발견(<c>docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md</c>)
/// 수정: <see cref="SessionRaidRunner.OnConnected"/>/<see cref="SessionRaidRunner.OnDisconnected"/>의
/// 방어적 분기(Player 컨텍스트 없음, 중복 SessionId, 미등록 세션 해제)가 지금까지 테스트되지 않았다.
/// 이 방어적 분기들은 정상 배선(<c>Main.cs</c>)에서는 발생하지 않지만, 향후 배선이 바뀌거나 리스너
/// 동작이 달라져도 조용히 안전하게 무시되어야 한다는 계약을 회귀로 고정한다.
/// </summary>
public class SessionRaidRunnerEdgeCaseTests
{
    private sealed class FakeSession : ISession
    {
        public Guid SessionId { get; init; } = Guid.NewGuid();
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

    private sealed class NoOpSessionRegistry : ISessionRegistry
    {
        public int Count => 0;
        public bool TryGet(Guid sessionId, out ISession? session) { session = null; return false; }
        public IReadOnlyCollection<ISession> GetAll() => Array.Empty<ISession>();
        public ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private static SessionRaidRunner CreateRunner(GameEventSink sink)
    {
        var levelSystem = PlayerLevelSystem.CreateDefault();
        var monsterTable = MonsterTable.CreateDefault();
        var equipmentTable = EquipmentTable.CreateDefault();
        return new SessionRaidRunner(
            levelSystem, monsterTable, equipmentTable, sink, new NoOpSessionRegistry(),
            raidTimeLimit: TimeSpan.FromMinutes(10), tickInterval: TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task OnConnected_SessionWithoutPlayerContext_CompletesWithoutThrowing()
    {
        var metrics = new GameMetrics($"Test.SessionRaidRunner.{Guid.NewGuid()}");
        await using var sink = new GameEventSink(TextWriter.Null, metrics);
        var runner = CreateRunner(sink);
        using var lifetimeCts = new CancellationTokenSource();
        runner.Start(lifetimeCts.Token);

        // Context가 비어 있음 — SessionPlayerBinder가 아직 실행되지 않은 상태를 흉내낸다.
        var session = new FakeSession { Context = null };

        await runner.OnConnected(session); // TryGetContext<Player> 실패 분기 — 예외 없이 조용히 반환해야 한다

        // 등록된 적 없는 세션의 해제도 안전한 no-op이어야 한다(TryRemove 실패 분기).
        await runner.OnDisconnected(session);

        lifetimeCts.Cancel();
    }

    [Fact]
    public async Task OnConnected_DuplicateSessionId_SecondCallIsNoOpAndDoesNotThrow()
    {
        var metrics = new GameMetrics($"Test.SessionRaidRunner.{Guid.NewGuid()}");
        await using var sink = new GameEventSink(TextWriter.Null, metrics);
        var runner = CreateRunner(sink);
        using var lifetimeCts = new CancellationTokenSource();
        runner.Start(lifetimeCts.Token);

        var levelSystem = PlayerLevelSystem.CreateDefault();
        var sharedSessionId = Guid.NewGuid();
        var sessionFirst = new FakeSession { SessionId = sharedSessionId, Context = PlayerFactory.CreateTemp(Guid.NewGuid(), levelSystem) };
        var sessionDuplicate = new FakeSession { SessionId = sharedSessionId, Context = PlayerFactory.CreateTemp(Guid.NewGuid(), levelSystem) };

        await runner.OnConnected(sessionFirst); // TryAdd 성공 — 제출 루프 시작
        await runner.OnConnected(sessionDuplicate); // 동일 SessionId — TryAdd 실패, ctx.Cts.Dispose() 후 즉시 반환

        // 정리: 첫 번째로 등록된 세션만 해제해도 예외가 없어야 한다.
        await runner.OnDisconnected(sessionFirst);
        // 중복 호출로는 애초에 등록되지 않았으므로 이 해제도 no-op이어야 한다.
        await runner.OnDisconnected(sessionDuplicate);

        lifetimeCts.Cancel();
    }

    [Fact]
    public async Task OnDisconnected_CalledTwiceForSameSession_SecondCallIsNoOpAndDoesNotThrow()
    {
        var metrics = new GameMetrics($"Test.SessionRaidRunner.{Guid.NewGuid()}");
        await using var sink = new GameEventSink(TextWriter.Null, metrics);
        var runner = CreateRunner(sink);
        using var lifetimeCts = new CancellationTokenSource();
        runner.Start(lifetimeCts.Token);

        var levelSystem = PlayerLevelSystem.CreateDefault();
        var session = new FakeSession { Context = PlayerFactory.CreateTemp(Guid.NewGuid(), levelSystem) };

        await runner.OnConnected(session);
        await runner.OnDisconnected(session); // 정상 해제 — Cts.Cancel() + 딕셔너리 제거
        await runner.OnDisconnected(session); // 두 번째 해제 — TryRemove 실패, Cancel() 재호출 없이 no-op이어야 한다

        lifetimeCts.Cancel();
    }
}
