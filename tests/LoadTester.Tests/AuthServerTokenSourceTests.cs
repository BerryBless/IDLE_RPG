using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using LoadTester.Auth;
using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace LoadTester.Tests;

/// <summary>
/// <see cref="AuthServerTokenSource"/>를 가짜 로그인 리스너(ServerLib만으로 구성 — 실제 AuthServer의
/// LoginRequest→LoginResponse 프로토콜 재현)에 붙여 성공·거부·캐시 재사용을 검증한다.
/// </summary>
public class AuthServerTokenSourceTests
{
    private static readonly BinaryPacketSerializer Serializer = new();

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>가짜 AuthServer: LoginRequest(10)를 받으면 조건에 따라 LoginResponse(11)를 돌려준다.</summary>
    private static IServerListener StartFakeAuthServer(int port, Func<LoginRequestPacket, LoginResponsePacket> respond)
    {
        IServerListener listener = ServerNet.CreateListener();
        listener.OnReceived = async (session, data) =>
        {
            ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
            if (packetId != LoginRequestPacket.Id)
                return;
            var request = Serializer.Deserialize<LoginRequestPacket>(data.Span);
            await session.SendAsync(respond(request));
        };
        listener.Start(port, IPAddress.Loopback);
        return listener;
    }

    [Fact]
    public async Task 로그인성공_토큰반환()
    {
        int port = GetFreePort();
        IServerListener listener = StartFakeAuthServer(port,
            request => new LoginResponsePacket { Success = true, Token = $"token-for-{request.Username}" });

        try
        {
            var source = new AuthServerTokenSource(
                "127.0.0.1", port, new CredentialProvider(3000), loginConcurrency: 4, TimeSpan.FromHours(1));

            TokenResult result = await source.AcquireAsync(7, CancellationToken.None);

            Assert.True(result.Success);
            Assert.Equal("token-for-user0007", result.Token);
            Assert.Equal(1, source.LoginRequestCount);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 로그인거부_실패결과_예외없음()
    {
        int port = GetFreePort();
        IServerListener listener = StartFakeAuthServer(port,
            _ => new LoginResponsePacket { Success = false, Token = string.Empty });

        try
        {
            var source = new AuthServerTokenSource(
                "127.0.0.1", port, new CredentialProvider(3000), loginConcurrency: 4, TimeSpan.FromHours(1));

            TokenResult result = await source.AcquireAsync(0, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Null(result.Token);
            Assert.NotNull(result.Error);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 서버미기동_실패결과_예외없음()
    {
        int port = GetFreePort(); // 아무도 리슨하지 않는 포트
        var source = new AuthServerTokenSource(
            "127.0.0.1", port, new CredentialProvider(3000), loginConcurrency: 4, TimeSpan.FromHours(1));

        TokenResult result = await source.AcquireAsync(0, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task 동일계정_반복획득은_캐시재사용으로_로그인1회()
    {
        int port = GetFreePort();
        IServerListener listener = StartFakeAuthServer(port,
            request => new LoginResponsePacket { Success = true, Token = $"token-for-{request.Username}" });

        try
        {
            var source = new AuthServerTokenSource(
                "127.0.0.1", port, new CredentialProvider(accountCount: 3), loginConcurrency: 4, TimeSpan.FromHours(1));

            // 클라이언트 0·3·6은 전부 계정 0으로 매핑 → 로그인은 1회, 나머지는 캐시.
            TokenResult first = await source.AcquireAsync(0, CancellationToken.None);
            TokenResult second = await source.AcquireAsync(3, CancellationToken.None);
            TokenResult third = await source.AcquireAsync(6, CancellationToken.None);

            Assert.True(first.Success && second.Success && third.Success);
            Assert.Equal(first.Token, second.Token);
            Assert.Equal(first.Token, third.Token);
            Assert.Equal(1, source.LoginRequestCount);

            // 다른 계정은 새 로그인.
            TokenResult other = await source.AcquireAsync(1, CancellationToken.None);
            Assert.True(other.Success);
            Assert.NotEqual(first.Token, other.Token);
            Assert.Equal(2, source.LoginRequestCount);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task 동시요청_동일계정_이중확인으로_로그인폭주없음()
    {
        int port = GetFreePort();
        IServerListener listener = StartFakeAuthServer(port,
            request => new LoginResponsePacket { Success = true, Token = $"token-for-{request.Username}" });

        try
        {
            var source = new AuthServerTokenSource(
                "127.0.0.1", port, new CredentialProvider(accountCount: 1), loginConcurrency: 1, TimeSpan.FromHours(1));

            // 전부 계정 0 — loginConcurrency=1이라 첫 로그인 후 나머지는 이중 확인에서 캐시 적중.
            TokenResult[] results = await Task.WhenAll(
                Enumerable.Range(0, 8).Select(i => source.AcquireAsync(i, CancellationToken.None).AsTask()));

            Assert.All(results, r => Assert.True(r.Success));
            Assert.Equal(1, source.LoginRequestCount);
        }
        finally
        {
            listener.Stop();
        }
    }
}
