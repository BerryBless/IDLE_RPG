using System.Net;
using System.Net.Sockets;
using AuthServer.Login;
using AuthServer.Security;
using ServerLib;
using ServerLib.Core.Auth;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace AuthServer.Tests;

/// <summary>
/// <see cref="AuthConnectionHandler"/>가 실제 루프백 소켓 위에서 로그인 요청을 올바르게 처리하는지
/// End-to-End로 검증한다. <c>tests/EchoExample.Tests/EchoEndToEndTests.cs</c>의 루프백 관례를 그대로 따른다.
/// </summary>
public class AuthServerEndToEndTests
{
    private static readonly BinaryPacketSerializer Serializer = new();
    private const int ResponseTimeoutMs = 5_000;
    private static readonly byte[] TestSecret = "e2e-test-secret"u8.ToArray();

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>
    /// AuthServer/Program.cs가 배선할 것과 동일한 방식으로 리스너 + AuthConnectionHandler를 구성한다.
    /// 소수 계정만 시딩된 인메모리 저장소를 사용한다.
    /// </summary>
    private static IServerListener StartAuthListener(int port, out LoginService login)
    {
        var repo = new InMemoryAccountRepository();
        var hasher = new Pbkdf2PasswordHasher(iterations: 1000);
        var codec = new HmacAuthTokenCodec(TestSecret);
        login = new LoginService(repo, hasher, codec, TimeSpan.FromMinutes(10));

        // 시딩은 동기적으로 완료를 기다린다(리스너 시작 전 계정이 준비돼 있어야 함).
        repo.InsertAsync(new AuthServer.Accounts.Account
        {
            AccountId = 1,
            Username = "alice",
            PasswordHash = hasher.Hash("alice-secret"),
            CreatedAtUtc = DateTime.UtcNow,
        }).AsTask().GetAwaiter().GetResult();

        var handler = new AuthConnectionHandler(login, Serializer);

        IServerListener listener = ServerNet.CreateListener();
        listener.OnReceived = handler.OnReceived;
        listener.Start(port, IPAddress.Loopback);
        return listener;
    }

    [Fact]
    public async Task LoginRequest_CorrectCredentials_ReturnsSuccessWithValidToken()
    {
        int port = GetFreePort();
        IServerListener listener = StartAuthListener(port, out _);

        try
        {
            var tcs = new TaskCompletionSource<LoginResponsePacket>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using IClientConnection client = ServerNet.CreateClient();
            client.OnReceived = (ReadOnlyMemory<byte> data) =>
            {
                tcs.TrySetResult(Serializer.Deserialize<LoginResponsePacket>(data.Span));
                return ValueTask.CompletedTask;
            };

            await client.ConnectAsync("127.0.0.1", port);
            await client.SendAsync(new LoginRequestPacket { Username = "alice", Password = "alice-secret" });

            LoginResponsePacket response = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(ResponseTimeoutMs));

            Assert.True(response.Success);
            Assert.False(string.IsNullOrEmpty(response.Token));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task LoginRequest_WrongPassword_ReturnsFailureWithEmptyToken()
    {
        int port = GetFreePort();
        IServerListener listener = StartAuthListener(port, out _);

        try
        {
            var tcs = new TaskCompletionSource<LoginResponsePacket>(TaskCreationOptions.RunContinuationsAsynchronously);

            await using IClientConnection client = ServerNet.CreateClient();
            client.OnReceived = (ReadOnlyMemory<byte> data) =>
            {
                tcs.TrySetResult(Serializer.Deserialize<LoginResponsePacket>(data.Span));
                return ValueTask.CompletedTask;
            };

            await client.ConnectAsync("127.0.0.1", port);
            await client.SendAsync(new LoginRequestPacket { Username = "alice", Password = "wrong-password" });

            LoginResponsePacket response = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(ResponseTimeoutMs));

            Assert.False(response.Success);
            Assert.Equal(string.Empty, response.Token);
        }
        finally
        {
            listener.Stop();
        }
    }
}
