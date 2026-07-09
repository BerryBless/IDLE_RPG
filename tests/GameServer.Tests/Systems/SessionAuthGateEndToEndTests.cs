using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Items;
using GameServer.Stats;
using GameServer.Systems;
using ServerLib;
using ServerLib.Core.Auth;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace GameServer.Tests.Systems;

/// <summary>
/// <c>Main.cs</c>의 신규 인증 게이트 배선(<see cref="AuthTokenPacket"/> 수신 → <see cref="SessionAuthGate"/>
/// 검증 → 성공 시에만 <see cref="SessionRaidRunner.OnConnected"/>로 레이드 참전)을 실제 루프백 TCP
/// 소켓으로 End-to-End 검증한다. <c>Main.cs</c>는 top-level 문이라 직접 호출할 수 없어(기존 테스트들과
/// 동일한 이유), 실제 프로덕션 클래스들을 Main.cs와 동일한 순서로 조합해 재현한다.
/// </summary>
public class SessionAuthGateEndToEndTests
{
    private static readonly byte[] TestSecret = "e2e-auth-gate-test-secret"u8.ToArray();
    private static readonly BinaryPacketSerializer Serializer = new();

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static MonsterTable BuildSmallRaidBossTable() => new(new List<MonsterTemplate>
    {
        new()
        {
            MonsterId = 7001, Name = "테스트 레이드 보스", Level = 20,
            Hp = 200, Atk = 0, Def = 0,
            ExpDrop = 100, GoldDrop = 200,
            DropTable = []
        }
    });

    private sealed record Fixture(
        IServerListener Listener, CancellationTokenSource LifetimeCts, GameEventSink Sink, int Port);

    private static Fixture StartServer()
    {
        var metrics = new GameMetrics($"Test.SessionAuthGate.E2E.{Guid.NewGuid()}");
        var sink = new GameEventSink(new StringWriter(), metrics);

        var levelSystem = PlayerLevelSystem.CreateDefault();
        var binder = new SessionPlayerBinder(levelSystem, sink);
        var registry = ServerNet.CreateSessionRegistry();
        var raidRunner = new SessionRaidRunner(
            levelSystem, BuildSmallRaidBossTable(), EquipmentTable.CreateDefault(), sink, registry,
            raidTimeLimit: TimeSpan.FromSeconds(30), tickInterval: TimeSpan.FromMilliseconds(5));

        var lifetimeCts = new CancellationTokenSource();
        raidRunner.Start(lifetimeCts.Token);

        var codec = new HmacAuthTokenCodec(TestSecret);
        var authGate = new SessionAuthGate(codec, levelSystem, sink, Serializer);

        int port = GetFreePort();
        IServerListener listener = ServerNet.CreateListener(registry);

        // Main.cs와 동일한 신규 배선: OnClientConnected에서 더 이상 binder.OnConnected를 부르지
        // 않는다 — 인증 성공 시에만 OnReceived에서 raidRunner.OnConnected를 잇는다.
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

    [Fact]
    public async Task ValidToken_ReceivesSuccessAckThenJoinsSharedRaid()
    {
        const int TimeoutMs = 5_000;
        var fixture = StartServer();

        try
        {
            var codec = new HmacAuthTokenCodec(TestSecret);
            string token = codec.Issue(accountId: 501, username: "e2e-alice", DateTimeOffset.UtcNow.AddMinutes(10));

            var ackReceived = new TaskCompletionSource<AuthTokenAckPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hpReceived = new TaskCompletionSource<MobHpPacket>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using IClientConnection client = ServerNet.CreateClient();
            client.OnReceived = data =>
            {
                ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
                if (packetId == AuthTokenAckPacket.Id)
                    ackReceived.TrySetResult(Serializer.Deserialize<AuthTokenAckPacket>(data.Span));
                else if (packetId == MobHpPacket.Id)
                    hpReceived.TrySetResult(Serializer.Deserialize<MobHpPacket>(data.Span));
                return ValueTask.CompletedTask;
            };

            await client.ConnectAsync("127.0.0.1", fixture.Port);
            await client.SendAsync(new AuthTokenPacket { Token = token });

            var ack = await ackReceived.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.True(ack.Success);

            // 인증 성공 이후에만 raidRunner.OnConnected가 걸려 보스 HP 브로드캐스트 대상이 된다
            // (첫 스텝은 스로틀 없이 즉시 브로드캐스트되므로 타임아웃 안에 반드시 도착).
            var hp = await hpReceived.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Equal(200, hp.MaxHp);
        }
        finally
        {
            fixture.Listener.Stop();
            fixture.LifetimeCts.Cancel();
            await fixture.Sink.DisposeAsync();
        }
    }

    [Fact]
    public async Task InvalidToken_ReceivesFailureAck()
    {
        const int TimeoutMs = 5_000;
        var fixture = StartServer();

        try
        {
            var ackReceived = new TaskCompletionSource<AuthTokenAckPacket>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using IClientConnection client = ServerNet.CreateClient();
            client.OnReceived = data =>
            {
                ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
                if (packetId == AuthTokenAckPacket.Id)
                    ackReceived.TrySetResult(Serializer.Deserialize<AuthTokenAckPacket>(data.Span));
                return ValueTask.CompletedTask;
            };

            await client.ConnectAsync("127.0.0.1", fixture.Port);
            await client.SendAsync(new AuthTokenPacket { Token = "totally-bogus-token" });

            var ack = await ackReceived.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.False(ack.Success);
        }
        finally
        {
            fixture.Listener.Stop();
            fixture.LifetimeCts.Cancel();
            await fixture.Sink.DisposeAsync();
        }
    }
}
