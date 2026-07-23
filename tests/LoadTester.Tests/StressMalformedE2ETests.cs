using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Stats;
using GameServer.Systems;
using LoadTester.Stress;
using ServerLib;
using ServerLib.Core.Auth;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace LoadTester.Tests;

/// <summary>
/// 악성 프레임을 실제 <see cref="SessionAuthGate"/> 배선 리스너(용량 모드 스타일: 레이드 없이 인증만)에
/// 루프백으로 쏘아, 서버가 크래시하지 않고(예외 미탈출) 이후 정상 클라이언트가 정상 인증되는지 E2E로 검증.
/// </summary>
public class StressMalformedE2ETests
{
    private static readonly byte[] TestSecret = "loadtester-stress-malformed-secret"u8.ToArray();
    private static readonly BinaryPacketSerializer Serializer = new();

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static (IServerListener Listener, GameEventSink Sink) StartAuthOnlyServer(int port)
    {
        var metrics = new GameMetrics($"Test.StressMalformed.{Guid.NewGuid()}");
        var sink = new GameEventSink(new StringWriter(), metrics);
        var levelSystem = PlayerLevelSystem.CreateDefault();
        var authGate = new SessionAuthGate(new HmacAuthTokenCodec(TestSecret), levelSystem, sink, Serializer);

        IServerListener listener = ServerNet.CreateListener(ServerNet.CreateSessionRegistry());
        // 용량 모드 스타일: 인증만(레이드 미배선).
        listener.OnReceived = async (session, data) => { _ = await authGate.HandleAsync(session, data); };
        listener.SessionSendTimeout = TimeSpan.FromSeconds(2);
        listener.Start(port, IPAddress.Loopback);
        return (listener, sink);
    }

    [Fact]
    public async Task 악성프레임_폭격후에도_서버생존_정상클라_인증성공()
    {
        int port = GetFreePort();
        var (listener, sink) = StartAuthOnlyServer(port);
        try
        {
            var rng = new Random(1);
            // 모든 악성 변종을 여러 연결로 쏜다.
            for (int round = 0; round < 3; round++)
            {
                await using IClientConnection bad = ServerNet.CreateClient();
                await bad.ConnectAsync("127.0.0.1", port);
                byte[][] frames =
                [
                    MalformedFrames.RandomGarbage(40, rng),
                    MalformedFrames.OversizedLengthHeader(),
                    MalformedFrames.TruncatedFrame(200, 10),
                    MalformedFrames.WrongPacketId(999),
                    MalformedFrames.ValidFrameGarbageBody(30, rng),
                    MalformedFrames.ZeroLengthBody(),
                    MalformedFrames.PartialHeader(),
                ];
                foreach (byte[] f in frames)
                    await bad.SendAsync(f);
                await Task.Delay(20);
            }

            // 서버가 살아있어야 한다: 정상 클라이언트가 인증에 성공.
            var codec = new HmacAuthTokenCodec(TestSecret);
            string token = codec.Issue(accountId: 7, username: "legit", DateTimeOffset.UtcNow.AddMinutes(10));
            var ackTcs = new TaskCompletionSource<AuthTokenAckPacket>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using IClientConnection good = ServerNet.CreateClient();
            good.OnReceived = data =>
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(data.Span) == AuthTokenAckPacket.Id)
                    ackTcs.TrySetResult(Serializer.Deserialize<AuthTokenAckPacket>(data.Span));
                return ValueTask.CompletedTask;
            };
            await good.ConnectAsync("127.0.0.1", port);
            await good.SendAsync(new AuthTokenPacket { Token = token });

            var ack = await ackTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(ack.Success); // 악성 폭격 후에도 서버가 정상 인증을 처리 = 생존
        }
        finally
        {
            listener.Stop();
            await sink.DisposeAsync();
        }
    }

    [Fact]
    public async Task 유효프레임_잘못된토큰_ack_false_연결유지()
    {
        int port = GetFreePort();
        var (listener, sink) = StartAuthOnlyServer(port);
        try
        {
            var ackTcs = new TaskCompletionSource<AuthTokenAckPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using IClientConnection client = ServerNet.CreateClient();
            client.OnReceived = data =>
            {
                if (BinaryPrimitives.ReadUInt16LittleEndian(data.Span) == AuthTokenAckPacket.Id)
                    ackTcs.TrySetResult(Serializer.Deserialize<AuthTokenAckPacket>(data.Span));
                return ValueTask.CompletedTask;
            };
            await client.ConnectAsync("127.0.0.1", port);
            // 유효 프레임이지만 가짜 토큰 → ack-false(연결은 유지).
            await client.SendAsync(new AuthTokenPacket { Token = "bogus-token" });

            var ack = await ackTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(ack.Success);
            Assert.True(client.IsConnected); // 거부는 연결 종료가 아니다
        }
        finally
        {
            listener.Stop();
            await sink.DisposeAsync();
        }
    }
}
