using System.Net;
using GameServer.Systems;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace GameServer.Tests.Systems;

/// <summary>
/// <see cref="TelemetryPublisher"/>의 "최신 스텝만 유지" 메일박스(<c>_bossLatestChannel</c>, 용량 1 +
/// DropOldest)와 퍼블리시 루프가 조립하는 <see cref="TelemetrySnapshotPacket"/> 필드 매핑을 소켓 없이
/// 단위 검증한다. <see cref="RaidBroadcasterTests"/>와 동일하게 가짜 <see cref="ISessionRegistry"/>로
/// 브로드캐스트 호출을 가로채 실제 바이트를 역직렬화해 확인한다.
/// </summary>
public class TelemetryPublisherTests
{
    /// <summary><see cref="TelemetryPublisher"/>가 읽는 3개 통계만 노출하는 가짜 리스너.</summary>
    private sealed class FakeServerListener : IServerListener
    {
        public bool IsRunning { get; set; }
        public Func<ISession, ValueTask>? OnClientConnected { get; set; }
        public Func<ISession, ValueTask>? OnClientDisconnected { get; set; }
        public Func<ISession, Exception, ValueTask>? OnClientError { get; set; }
        public Func<ISession, ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }
        public TimeSpan? IdleTimeout { get; set; }
        public Func<ISession, ValueTask>? OnIdleTimeout { get; set; }
        public int? MaxConnections { get; set; }
        public int? MaxConnectionsPerIp { get; set; }
        public long TotalRejectedConnections { get; set; }
        public TimeSpan? SessionSendTimeout { get; set; }
        public int ActiveSessionCount { get; set; }

        public void Start(int port) => throw new NotSupportedException("테스트에서 사용하지 않음");
        public void Start(int port, IPAddress bindAddress) => throw new NotSupportedException("테스트에서 사용하지 않음");
        public void Stop() => throw new NotSupportedException("테스트에서 사용하지 않음");
    }

    /// <summary>BroadcastAsync로 전송된 원시 바이트를 순서대로 기록만 하는 가짜 레지스트리.</summary>
    private sealed class RecordingSessionRegistry : ISessionRegistry
    {
        private readonly List<byte[]> _broadcasts = [];
        public IReadOnlyList<byte[]> Broadcasts => _broadcasts;

        public int Count => 0;
        public bool TryGet(Guid sessionId, out ISession? session) { session = null; return false; }
        public IReadOnlyCollection<ISession> GetAll() => [];

        public ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            lock (_broadcasts)
            {
                _broadcasts.Add(data.ToArray()); // ToArray: 버퍼가 호출 직후 반납되므로 반드시 깊은복사 보관
            }
            return ValueTask.CompletedTask;
        }
    }

    private static readonly BinaryPacketSerializer Serializer = new();

    private static async Task<IReadOnlyList<byte[]>> RunPublishLoopBrieflyAsync(
        TelemetryPublisher publisher, RecordingSessionRegistry registry, TimeSpan wait)
    {
        using var cts = new CancellationTokenSource();
        var loopTask = publisher.PublishLoopAsync(cts.Token);
        await Task.Delay(wait);
        cts.Cancel();
        try { await loopTask; } catch (OperationCanceledException) { }
        return registry.Broadcasts;
    }

    [Fact]
    public async Task PublishLoop_BroadcastsListenerStats_WhenNoStepObservedYet()
    {
        var listener = new FakeServerListener { IsRunning = true, ActiveSessionCount = 3, TotalRejectedConnections = 5 };
        var registry = new RecordingSessionRegistry();
        var publisher = new TelemetryPublisher(listener, registry, publishInterval: TimeSpan.FromMilliseconds(30));

        var broadcasts = await RunPublishLoopBrieflyAsync(publisher, registry, TimeSpan.FromMilliseconds(100));

        Assert.NotEmpty(broadcasts);
        var packet = Serializer.Deserialize<TelemetrySnapshotPacket>(broadcasts[0]);
        Assert.Equal(3, packet.ConnectedCount);
        Assert.True(packet.IsRunning);
        Assert.Equal(5, packet.RejectedConnections);
        // default(RaidStepBroadcast) — 아직 onStep이 한 번도 없었던 상태(서버 기동 직후)의 방어적 기본값.
        Assert.Equal(0, packet.BossCurrentHp);
        Assert.Equal(0, packet.BossMaxHp);
        Assert.Equal(string.Empty, packet.MvpName);
    }

    [Fact]
    public async Task PublishLoop_ReflectsLatestBossStep_AfterOnStep()
    {
        var listener = new FakeServerListener { IsRunning = true, ActiveSessionCount = 1 };
        var registry = new RecordingSessionRegistry();
        var publisher = new TelemetryPublisher(listener, registry, publishInterval: TimeSpan.FromMilliseconds(30));

        await publisher.OnStep(
            new RaidStepBroadcast(RaidEventType.BossDamaged, CurrentHp: 4000, MaxHp: 5000, DeadGeneration: 1, NewGeneration: 1, MvpName: string.Empty, TopDamage: 0),
            CancellationToken.None);

        var broadcasts = await RunPublishLoopBrieflyAsync(publisher, registry, TimeSpan.FromMilliseconds(100));

        Assert.NotEmpty(broadcasts);
        var packet = Serializer.Deserialize<TelemetrySnapshotPacket>(broadcasts[^1]);
        Assert.Equal(4000, packet.BossCurrentHp);
        Assert.Equal(5000, packet.BossMaxHp);
        Assert.Equal(1, packet.Generation);
    }

    [Fact]
    public async Task OnStep_RapidCallsBeforeConsumption_OnlyLatestStepSurvives()
    {
        var listener = new FakeServerListener();
        var registry = new RecordingSessionRegistry();
        // 퍼블리시 루프 시작 전 3연속 OnStep — 용량 1 + DropOldest이므로 마지막 값만 남아야 한다.
        var publisher = new TelemetryPublisher(listener, registry, publishInterval: TimeSpan.FromMilliseconds(200));

        await publisher.OnStep(new RaidStepBroadcast(RaidEventType.BossDamaged, 100, 1000, 1, 1, string.Empty, 0), CancellationToken.None);
        await publisher.OnStep(new RaidStepBroadcast(RaidEventType.BossDamaged, 50, 1000, 1, 1, string.Empty, 0), CancellationToken.None);
        await publisher.OnStep(new RaidStepBroadcast(RaidEventType.BossDamaged, 10, 1000, 1, 1, string.Empty, 0), CancellationToken.None);

        var broadcasts = await RunPublishLoopBrieflyAsync(publisher, registry, TimeSpan.FromMilliseconds(250));

        Assert.NotEmpty(broadcasts);
        var packet = Serializer.Deserialize<TelemetrySnapshotPacket>(broadcasts[0]);
        Assert.Equal(10, packet.BossCurrentHp);
    }

    [Fact]
    public async Task PublishLoop_BossDefeatedStep_CarriesMvpNameAndTopDamage()
    {
        var listener = new FakeServerListener();
        var registry = new RecordingSessionRegistry();
        var publisher = new TelemetryPublisher(listener, registry, publishInterval: TimeSpan.FromMilliseconds(30));

        await publisher.OnStep(
            new RaidStepBroadcast(RaidEventType.BossDefeated, CurrentHp: 0, MaxHp: 5000, DeadGeneration: 1, NewGeneration: 2, MvpName: "테스트유저", TopDamage: 12345),
            CancellationToken.None);

        var broadcasts = await RunPublishLoopBrieflyAsync(publisher, registry, TimeSpan.FromMilliseconds(100));

        Assert.NotEmpty(broadcasts);
        var packet = Serializer.Deserialize<TelemetrySnapshotPacket>(broadcasts[^1]);
        Assert.Equal((byte)RaidEventType.BossDefeated, packet.LastEvent);
        Assert.Equal("테스트유저", packet.MvpName);
        Assert.Equal(12345, packet.TopDamage);
        Assert.Equal(2, packet.Generation);
    }
}
