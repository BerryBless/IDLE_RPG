using System.Net;
using System.Net.Sockets;
using LoadTester.Stress;
using ServerLib;
using ServerLib.Interface;

namespace LoadTester.Tests;

/// <summary>
/// 리스너의 세션당 프레임 레이트 리밋(<see cref="IServerListener.SessionMaxFramesPerSecond"/>)이
/// 완전 프레임을 과다 전송하는 세션을 실제로 끊고, 정상(저빈도) 세션은 유지하는지 루프백 E2E로 검증.
/// </summary>
public class FrameRateLimitE2ETests
{
    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static IServerListener StartServer(int port, int maxFramesPerSecond)
    {
        IServerListener listener = ServerNet.CreateListener(ServerNet.CreateSessionRegistry());
        // 완전 프레임이지만 인증 패킷이 아닌 것은 서버가 조용히 무시(자동 축출 안 됨) → 레이트 리밋만이
        // 이 세션을 끊는 유일한 방어. OnReceived는 무처리(수신만).
        listener.OnReceived = (_, _) => ValueTask.CompletedTask;
        listener.SessionMaxFramesPerSecond = maxFramesPerSecond;
        listener.Start(port, IPAddress.Loopback);
        return listener;
    }

    [Fact]
    public async Task 완전프레임_과다전송_세션_레이트리밋으로_끊긴다()
    {
        int port = GetFreePort();
        IServerListener listener = StartServer(port, maxFramesPerSecond: 50);
        try
        {
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using IClientConnection client = ServerNet.CreateClient();
            client.OnDisconnected = () => { disconnected.TrySetResult(); return ValueTask.CompletedTask; };
            await client.ConnectAsync("127.0.0.1", port);

            // 완전한(무시되는) 프레임을 상한(50/s)보다 훨씬 많이 빠르게 쏜다 → 서버가 이 세션을 끊어야 한다.
            // 서버가 세션을 끊으면 진행 중 SendAsync가 SocketException으로 실패할 수 있다(정상 — 끊김 신호).
            try
            {
                for (int i = 0; i < 2000 && client.IsConnected; i++)
                    await client.SendAsync(MalformedFrames.WrongPacketId(999)); // 완전 프레임, 서버는 무시
            }
            catch (SocketException) { /* 서버가 끊으며 송신 실패 — 기대된 결과 */ }
            catch (InvalidOperationException) { /* 이미 끊긴 연결로 송신 시도 */ }

            await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5)); // 끊기지 않으면 타임아웃 → 실패
            Assert.True(disconnected.Task.IsCompletedSuccessfully);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 저빈도_정상세션은_유지된다()
    {
        int port = GetFreePort();
        IServerListener listener = StartServer(port, maxFramesPerSecond: 50);
        try
        {
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using IClientConnection client = ServerNet.CreateClient();
            client.OnDisconnected = () => { disconnected.TrySetResult(); return ValueTask.CompletedTask; };
            await client.ConnectAsync("127.0.0.1", port);

            // 초당 5프레임(상한 50 하회)을 1.5초간 → 끊기면 안 된다.
            for (int i = 0; i < 8; i++)
            {
                await client.SendAsync(MalformedFrames.WrongPacketId(999));
                await Task.Delay(200);
            }

            Assert.True(client.IsConnected);
            Assert.False(disconnected.Task.IsCompleted);
        }
        finally
        {
            listener.Stop();
        }
    }
}
