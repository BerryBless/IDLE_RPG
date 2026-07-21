using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AuthServer.Accounts;
using AuthServer.Login;
using AuthServer.Security;
using GameServer.Stats;
using GameServer.Systems;
using MonitorServer;
using ServerLib;
using ServerLib.Core.Auth;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace FullStack.Tests;

/// <summary>
/// 전체 스택 통합 테스트: AuthServer(로그인) + GameServer(HMAC 토큰 게이트 + 텔레메트리) +
/// MonitorServer(텔레메트리 구독)를 한 프로세스·루프백에 조합해, 실제 클라이언트가
/// login→token→game-auth→telemetry→monitor 전 경로를 통과하는지 End-to-End로 검증한다.
/// 각 서버의 top-level Program/Main은 직접 호출할 수 없어(기존 E2E 테스트들과 동일 이유) 프로덕션
/// 클래스를 프로덕션과 동일 순서로 조합해 재현한다. AuthServer는 인메모리 계정(Mongo 불필요),
/// MonitorServer는 웹(Kestrel) 없이 TelemetryClientLoop→TelemetrySnapshotStore만 구동한다.
/// </summary>
public sealed class FullStackE2ETests : IDisposable
{
    // AuthServer(발급)와 GameServer(검증)가 공유하는 HMAC 비밀키 — 동일 값이어야 토큰이 검증된다.
    private static readonly byte[] SharedSecret = "fullstack-e2e-shared-hmac-secret-32b!"u8.ToArray();
    private static readonly BinaryPacketSerializer Serializer = new();

    private readonly CancellationTokenSource _cts = new();
    private readonly List<IServerListener> _listeners = new();
    private GameEventSink? _sink;

    private int _authPort;
    private int _gamePort;
    private int _telemetryPort;
    private IServerListener _gameListener = null!;
    private readonly Pbkdf2PasswordHasher _hasher = new(iterations: 1000); // 테스트 속도용 저반복

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>전체 스택을 조합·기동한다. 계정을 시딩한 뒤 Auth·Game·Telemetry 리스너를 연다.</summary>
    private void StartStack(int accountCount)
    {
        var repo = new InMemoryAccountRepository();
        for (int i = 0; i < accountCount; i++)
        {
            repo.InsertAsync(new Account
            {
                AccountId = i + 1,
                Username = $"user{i:D4}",
                PasswordHash = _hasher.Hash($"Pass!{i:D4}"),
                CreatedAtUtc = DateTime.UtcNow,
            }).AsTask().GetAwaiter().GetResult();
        }

        // ── AUTH ──
        var authCodec = new HmacAuthTokenCodec(SharedSecret);
        var login = new LoginService(repo, _hasher, authCodec, TimeSpan.FromMinutes(10));
        var authHandler = new AuthConnectionHandler(login, Serializer);
        _authPort = GetFreePort();
        IServerListener authListener = ServerNet.CreateListener();
        authListener.OnReceived = authHandler.OnReceived;
        authListener.Start(_authPort, IPAddress.Loopback);
        _listeners.Add(authListener);

        // ── GAME (토큰 게이트 + 텔레메트리, 레이드는 생략 — 용량 모드 스타일) ──
        var levelSystem = PlayerLevelSystem.CreateDefault();
        _sink = new GameEventSink(new StringWriter(), new GameMetrics($"FullStack.{Guid.NewGuid()}"));
        var gameCodec = new HmacAuthTokenCodec(SharedSecret); // AUTH와 동일 비밀키
        var authGate = new SessionAuthGate(gameCodec, levelSystem, _sink, Serializer);

        var gameRegistry = ServerNet.CreateSessionRegistry();
        var telemetryRegistry = ServerNet.CreateSessionRegistry();
        _gameListener = ServerNet.CreateListener(gameRegistry);
        IServerListener telemetryListener = ServerNet.CreateListener(telemetryRegistry);

        // TelemetryPublisher: 게임 리스너의 ActiveSessionCount를 짧은 주기로 텔레메트리 구독자에게 발행.
        var publisher = new TelemetryPublisher(_gameListener, telemetryRegistry, TimeSpan.FromMilliseconds(50));
        _gameListener.OnReceived = async (session, data) => { _ = await authGate.HandleAsync(session, data); };
        _ = Task.Run(() => publisher.PublishLoopAsync(_cts.Token));

        _gamePort = GetFreePort();
        _telemetryPort = GetFreePort();
        _gameListener.Start(_gamePort, IPAddress.Loopback);
        telemetryListener.Start(_telemetryPort, IPAddress.Loopback);
        _listeners.Add(_gameListener);
        _listeners.Add(telemetryListener);
    }

    /// <summary>MonitorServer의 텔레메트리 구독 루프를 웹 없이 기동하고 스냅샷 스토어를 반환한다.</summary>
    private TelemetrySnapshotStore StartMonitor()
    {
        var store = new TelemetrySnapshotStore();
        _ = Task.Run(() => TelemetryClientLoop.RunAsync(
            "127.0.0.1", _telemetryPort, store, TimeSpan.FromMilliseconds(100), _cts.Token));
        return store;
    }

    /// <summary>AuthServer에 로그인해 토큰을 받는다. 실패 시 null.</summary>
    private async Task<string?> LoginAsync(string username, string password)
    {
        var responseTcs = new TaskCompletionSource<LoginResponsePacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using IClientConnection client = ServerNet.CreateClient();
        client.OnReceived = data =>
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(data.Span) == LoginResponsePacket.Id)
                responseTcs.TrySetResult(Serializer.Deserialize<LoginResponsePacket>(data.Span));
            return ValueTask.CompletedTask;
        };
        await client.ConnectAsync("127.0.0.1", _authPort);
        await client.SendAsync(new LoginRequestPacket { Username = username, Password = password });
        LoginResponsePacket response = await responseTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return response.Success ? response.Token : null;
    }

    /// <summary>토큰으로 GameServer에 인증하고, 인증 성공 시 연결을 유지한 채 반환한다(await using으로 정리).</summary>
    private async Task<(IClientConnection Client, bool Authenticated)> AuthenticateToGameAsync(string token)
    {
        var ackTcs = new TaskCompletionSource<AuthTokenAckPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        IClientConnection client = ServerNet.CreateClient();
        client.OnReceived = data =>
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(data.Span) == AuthTokenAckPacket.Id)
                ackTcs.TrySetResult(Serializer.Deserialize<AuthTokenAckPacket>(data.Span));
            return ValueTask.CompletedTask;
        };
        await client.ConnectAsync("127.0.0.1", _gamePort);
        await client.SendAsync(new AuthTokenPacket { Token = token });
        AuthTokenAckPacket ack = await ackTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        return (client, ack.Success);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "조건이 제한 시간 내에 충족되지 않았습니다.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task 로그인_토큰_게임인증_모니터반영_전경로()
    {
        StartStack(accountCount: 1);
        TelemetrySnapshotStore monitor = StartMonitor();

        // 모니터가 게임 텔레메트리에 붙을 때까지 대기(초기 접속 0).
        await WaitUntilAsync(() => monitor.Current.Connected, TimeSpan.FromSeconds(5));

        // 1) AuthServer 로그인 → 토큰.
        string? token = await LoginAsync("user0000", "Pass!0000");
        Assert.NotNull(token);

        // 2) 토큰으로 GameServer 인증.
        var (client, authenticated) = await AuthenticateToGameAsync(token!);
        await using (client)
        {
            Assert.True(authenticated);

            // 3) MonitorServer가 텔레메트리로 접속 수를 반영(게임 세션 1개).
            await WaitUntilAsync(() => monitor.Current.ConnectedCount >= 1, TimeSpan.FromSeconds(5));
            Assert.True(monitor.Current.IsRunning);
        }
    }

    [Fact]
    public async Task 잘못된_비밀번호_로그인실패_토큰없음()
    {
        StartStack(accountCount: 1);
        string? token = await LoginAsync("user0000", "wrong-password");
        Assert.Null(token); // 로그인 실패 → 토큰 발급 안 됨 → 게임 인증 불가
    }

    [Fact]
    public async Task 위조_토큰_게임인증_거부()
    {
        StartStack(accountCount: 1);
        var (client, authenticated) = await AuthenticateToGameAsync("forged.token.not-from-authserver");
        await using (client)
            Assert.False(authenticated); // AuthServer가 발급하지 않은 토큰은 게이트가 거부
    }

    [Fact]
    public async Task 다중_클라이언트_모니터_접속수_반영()
    {
        const int Clients = 3;
        StartStack(accountCount: Clients);
        TelemetrySnapshotStore monitor = StartMonitor();
        await WaitUntilAsync(() => monitor.Current.Connected, TimeSpan.FromSeconds(5));

        var held = new List<IClientConnection>();
        try
        {
            for (int i = 0; i < Clients; i++)
            {
                string? token = await LoginAsync($"user{i:D4}", $"Pass!{i:D4}");
                Assert.NotNull(token);
                var (client, authenticated) = await AuthenticateToGameAsync(token!);
                Assert.True(authenticated);
                held.Add(client);
            }

            // 모니터가 전 클라이언트를 반영.
            await WaitUntilAsync(() => monitor.Current.ConnectedCount >= Clients, TimeSpan.FromSeconds(5));
        }
        finally
        {
            foreach (IClientConnection c in held)
                await c.DisposeAsync();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (IServerListener l in _listeners)
        {
            try { l.Stop(); } catch (Exception) { }
        }
        try { _sink?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception) { }
        _cts.Dispose();
    }
}
