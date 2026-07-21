using System.Net;
using System.Net.Sockets;
using GameServer.Items;
using GameServer.Stats;
using GameServer.Systems;
using LoadTester.Auth;
using LoadTester.Client;
using LoadTester.Metrics;
using LoadTester.Options;
using ServerLib;
using ServerLib.Core.Auth;
using ServerLib.Core.Serialization;
using ServerLib.Interface;

namespace LoadTester.Tests;

/// <summary>
/// <see cref="VirtualClient"/>+<see cref="LoadController"/>를 인프로세스 GameServer 픽스처
/// (Main.cs와 동일하게 SessionAuthGate → SessionRaidRunner 배선, SessionAuthGateEndToEndTests 패턴)에
/// 붙여 실제 루프백 소켓으로 인증·브로드캐스트 수신·끊김 분류·정상 종료를 End-to-End 검증한다.
/// </summary>
public class VirtualClientEndToEndTests
{
    private static readonly byte[] TestSecret = "loadtester-e2e-test-secret"u8.ToArray();
    private static readonly BinaryPacketSerializer Serializer = new();

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static MonsterTable BuildRaidBossTable() => new(new List<MonsterTemplate>
    {
        new()
        {
            MonsterId = 7001, Name = "부하 테스트 보스", Level = 20,
            Hp = 50_000_000, Atk = 0, Def = 0,
            ExpDrop = 100, GoldDrop = 200,
            DropTable = []
        }
    });

    private sealed record Fixture(
        IServerListener Listener, CancellationTokenSource LifetimeCts, GameEventSink Sink, int Port);

    private static Fixture StartServer()
    {
        var metrics = new GameMetrics($"Test.LoadTester.E2E.{Guid.NewGuid()}");
        var sink = new GameEventSink(new StringWriter(), metrics);

        var levelSystem = PlayerLevelSystem.CreateDefault();
        var binder = new SessionPlayerBinder(levelSystem, sink);
        var registry = ServerNet.CreateSessionRegistry();
        var raidRunner = new SessionRaidRunner(
            levelSystem, BuildRaidBossTable(), EquipmentTable.CreateDefault(), sink, registry,
            raidTimeLimit: TimeSpan.FromSeconds(60), tickInterval: TimeSpan.FromMilliseconds(10));

        var lifetimeCts = new CancellationTokenSource();
        raidRunner.Start(lifetimeCts.Token);

        var authGate = new SessionAuthGate(new HmacAuthTokenCodec(TestSecret), levelSystem, sink, Serializer);

        int port = GetFreePort();
        IServerListener listener = ServerNet.CreateListener(registry);
        listener.OnReceived = async (session, data) =>
        {
            bool justAuthenticated = await authGate.HandleAsync(session, data);
            if (justAuthenticated)
                await raidRunner.OnConnected(session);
        };
        listener.OnClientDisconnected = async session =>
        {
            await raidRunner.OnDisconnected(session);
            await binder.OnDisconnected(session);
        };
        listener.OnClientError = binder.OnError;
        listener.Start(port, IPAddress.Loopback);

        return new Fixture(listener, lifetimeCts, sink, port);
    }

    private static LoadTestOptions BuildOptions(int port, int clients) => new()
    {
        Mode = "game",
        Clients = clients,
        RampUpPerSecond = 100,
        GamePort = port,
        PingInterval = TimeSpan.FromMilliseconds(200),
        AuthTimeout = TimeSpan.FromSeconds(5),
        StallTimeout = TimeSpan.FromSeconds(5),
        ReconnectDelay = TimeSpan.FromMilliseconds(200),
        NoTelemetry = true,
    };

    private static (LoadController Controller, MetricsAggregator Metrics) BuildLoadTester(
        LoadTestOptions options)
    {
        var tokenSource = new LocalHmacTokenSource(
            new HmacAuthTokenCodec(TestSecret), new CredentialProvider(options.Accounts), options.TokenTtl);
        var metrics = new MetricsAggregator();
        var controller = new LoadController(options, tokenSource, metrics);
        return (controller, metrics);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "대기 조건이 제한 시간 내에 충족되지 않았습니다.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task 다중클라이언트_전원인증_브로드캐스트수신_정상종료()
    {
        const int ClientCount = 5;
        var fixture = StartServer();
        try
        {
            var options = BuildOptions(fixture.Port, ClientCount);
            var (controller, metrics) = BuildLoadTester(options);

            using var loadCts = new CancellationTokenSource();
            Task runTask = controller.RunAsync(loadCts.Token);

            // 전원 인증 + 브로드캐스트 수신까지 대기.
            await WaitUntilAsync(
                () => metrics.SnapshotTotals().AuthSuccesses >= ClientCount
                      && metrics.Broadcasts.Sum() >= ClientCount,
                TimeSpan.FromSeconds(15));

            // 스냅샷 검증: 전원 연결·인증 상태, RTT는 PING 주기 경과 후 갱신됨.
            await WaitUntilAsync(
                () => controller.Clients.All(c =>
                {
                    var snap = c.ReadSnapshot();
                    return snap is { Connected: true, Authenticated: true, EverAuthenticated: true };
                }),
                TimeSpan.FromSeconds(5));

            var totals = metrics.SnapshotTotals();
            Assert.Equal(0, totals.ConnectFailures);
            Assert.Equal(0, totals.AuthFailures);
            Assert.Equal(0, totals.AuthTimeouts);
            Assert.True(metrics.BytesIn.Sum() > 0);

            // 정상 종료: 취소 후 태스크가 시간 내 완료되고 예기치 않은 끊김으로 집계되지 않는다.
            loadCts.Cancel();
            await runTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, metrics.SnapshotTotals().UnexpectedDisconnects);
        }
        finally
        {
            fixture.Listener.Stop();
            fixture.LifetimeCts.Cancel();
            await fixture.Sink.DisposeAsync();
        }
    }

    [Fact]
    public async Task RTT_핑퐁주기후_스냅샷에_측정된다()
    {
        var fixture = StartServer();
        try
        {
            var options = BuildOptions(fixture.Port, clients: 2);
            var (controller, metrics) = BuildLoadTester(options);

            using var loadCts = new CancellationTokenSource();
            Task runTask = controller.RunAsync(loadCts.Token);

            // PingInterval 200ms → 2주기 이상 지나면 RTT가 0보다 커야 한다.
            await WaitUntilAsync(
                () => controller.Clients.All(c =>
                {
                    var snap = c.ReadSnapshot();
                    return snap.Connected && snap.RttTicks > 0;
                }),
                TimeSpan.FromSeconds(10));

            loadCts.Cancel();
            await runTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            fixture.Listener.Stop();
            fixture.LifetimeCts.Cancel();
            await fixture.Sink.DisposeAsync();
        }
    }

    [Fact]
    public async Task 서버중단_예기치않은끊김으로_분류되고_재접속을_시도한다()
    {
        var fixture = StartServer();
        bool listenerStopped = false;
        try
        {
            var options = BuildOptions(fixture.Port, clients: 2);
            var (controller, metrics) = BuildLoadTester(options);

            using var loadCts = new CancellationTokenSource();
            Task runTask = controller.RunAsync(loadCts.Token);

            await WaitUntilAsync(
                () => metrics.SnapshotTotals().AuthSuccesses >= 2, TimeSpan.FromSeconds(15));

            // 서버 강제 중단 → 클라이언트는 예기치 않은 끊김으로 집계하고 재접속 루프에 들어간다.
            fixture.Listener.Stop();
            listenerStopped = true;

            await WaitUntilAsync(
                () =>
                {
                    var totals = metrics.SnapshotTotals();
                    return totals.UnexpectedDisconnects >= 2 && totals.Reconnects >= 2;
                },
                TimeSpan.FromSeconds(15));

            loadCts.Cancel();
            await runTask.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            if (!listenerStopped)
                fixture.Listener.Stop();
            fixture.LifetimeCts.Cancel();
            await fixture.Sink.DisposeAsync();
        }
    }
}
