using System.Buffers.Binary;
using System.Collections.Concurrent;
using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace LoadTester.Auth;

/// <summary>
/// full 모드용 토큰 소스: 실제 AuthServer(기본 7778)에 <see cref="LoginRequestPacket"/>을 보내
/// <see cref="LoginResponsePacket"/>으로 토큰을 획득합니다. 두 가지 보호 장치를 내장한다 —
/// (1) 동시 로그인 상한: AuthServer의 PBKDF2 검증은 CPU 집약적이고 로그인마다 임시 포트가
/// TIME_WAIT로 소모되므로 세마포어로 동시 요청을 제한한다.
/// (2) 계정별 토큰 캐시: 3000계정을 10k+ 클라이언트가 공유하므로, 만료 5분 전까지는 같은 계정의
/// 기존 토큰을 재사용해 대량 재접속 시 로그인 폭주를 계정 수 이하로 붕괴시킨다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 캐시는 ConcurrentDictionary, 로그인
/// 경로는 세마포어 직렬화 + 이중 확인으로 동일 계정 중복 로그인을 최소화한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 로그인마다 클라이언트 연결·패킷·문자열 할당
/// (저빈도: 캐시 미스 시에만).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking(비동기). AuthServer 왕복 동안 비동기 대기.</description></item>
/// </list>
/// </remarks>
public sealed class AuthServerTokenSource : ITokenSource
{
    /// <summary>캐시된 토큰의 재사용을 중단하는 잔여 유효기간 하한.</summary>
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LoginResponseTimeout = TimeSpan.FromSeconds(10);

    // BinaryPacketSerializer: 무상태라 모든 로그인 시도가 단일 인스턴스를 공유해도 안전
    // (MonitorServer TelemetryClientLoop와 동일 근거).
    private static readonly BinaryPacketSerializer Serializer = new();

    private readonly string _host;
    private readonly int _port;
    private readonly CredentialProvider _credentials;
    private readonly TimeSpan _tokenTtl;

    // SemaphoreSlim: 커널 전환 없이 스핀-대기 후 관리형 대기로 전환하는 경량 세마포어.
    // 수천 태스크가 동시에 로그인을 요구해도 WaitAsync 큐에 비동기로 줄 세워
    // 스레드를 점유하지 않으면서 AuthServer로 나가는 동시 요청 수를 상한한다.
    private readonly SemaphoreSlim _loginGate;

    // ConcurrentDictionary: 계정 수(≤수천)로 크기가 상한된 lock-free 해시맵.
    // 다수 클라이언트 태스크가 동시에 조회·갱신해도 세그먼트 CAS로 경합을 흡수한다.
    private readonly ConcurrentDictionary<int, CachedToken> _tokenCache = new();

    private readonly record struct CachedToken(string Token, long ExpiresUnixSeconds);

    /// <summary>토큰 소스를 생성합니다.</summary>
    /// <param name="host">AuthServer 호스트.</param>
    /// <param name="port">AuthServer 포트.</param>
    /// <param name="credentials">클라이언트→계정 매핑.</param>
    /// <param name="loginConcurrency">AuthServer 동시 로그인 상한.</param>
    /// <param name="tokenTtl">AuthServer 발급 토큰의 유효기간(캐시 만료 계산용 — AuthServerConfig.TokenLifetime과 일치해야 함).</param>
    public AuthServerTokenSource(string host, int port, CredentialProvider credentials,
        int loginConcurrency, TimeSpan tokenTtl)
    {
        _host = host;
        _port = port;
        _credentials = credentials;
        _tokenTtl = tokenTtl;
        _loginGate = new SemaphoreSlim(loginConcurrency, loginConcurrency);
    }

    /// <summary>지금까지 AuthServer로 실제 전송한 로그인 요청 수(캐시 적중은 제외). 테스트·통계용.</summary>
    public long LoginRequestCount => Interlocked.Read(ref _loginRequestCount);
    private long _loginRequestCount;

    /// <inheritdoc/>
    public async ValueTask<TokenResult> AcquireAsync(int clientIndex, CancellationToken cancellationToken)
    {
        Credential credential = _credentials.GetFor(clientIndex);

        if (TryGetCached(credential.AccountIndex, out string cached))
            return TokenResult.Ok(cached);

        await _loginGate.WaitAsync(cancellationToken);
        try
        {
            // 이중 확인: 세마포어 대기 중 같은 계정의 다른 클라이언트가 이미 로그인을 끝냈다면
            // 그 토큰을 재사용한다 — 동일 계정 중복 로그인을 캐시로 흡수.
            if (TryGetCached(credential.AccountIndex, out cached))
                return TokenResult.Ok(cached);

            return await LoginAsync(credential, cancellationToken);
        }
        finally
        {
            _loginGate.Release();
        }
    }

    private bool TryGetCached(int accountIndex, out string token)
    {
        token = string.Empty;
        if (!_tokenCache.TryGetValue(accountIndex, out CachedToken entry))
            return false;
        long remaining = entry.ExpiresUnixSeconds - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (remaining <= (long)ExpiryMargin.TotalSeconds)
            return false;
        token = entry.Token;
        return true;
    }

    private async ValueTask<TokenResult> LoginAsync(Credential credential, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _loginRequestCount);

        // await using: IClientConnection 콜백은 ConnectAsync 전에만 설정 가능하므로(계약)
        // 로그인 시도마다 새 인스턴스를 만들고 응답 수신 후 즉시 해제한다.
        await using IClientConnection client = ServerNet.CreateClient();

        // TaskCompletionSource(RunContinuationsAsynchronously): 응답 도착(I/O 스레드)과
        // 이 메서드(클라이언트 태스크) 사이의 유일한 통신 수단. 연속 작업이 I/O 스레드에서
        // 동기 실행돼 수신 루프를 점유하는 것을 방지한다.
        var responseReceived = new TaskCompletionSource<LoginResponsePacket>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        client.OnReceived = data =>
        {
            // ReadOnlyMemory는 콜백 반환 후 무효 — 즉시 역직렬화까지 끝낸다.
            ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
            if (packetId == LoginResponsePacket.Id)
                responseReceived.TrySetResult(Serializer.Deserialize<LoginResponsePacket>(data.Span));
            return ValueTask.CompletedTask;
        };
        client.OnDisconnected = () =>
        {
            responseReceived.TrySetException(
                new InvalidOperationException("로그인 응답 수신 전 AuthServer 연결이 끊겼습니다."));
            return ValueTask.CompletedTask;
        };

        try
        {
            await client.ConnectAsync(_host, _port, cancellationToken);
            await client.SendAsync(new LoginRequestPacket
            {
                Username = credential.Username,
                Password = credential.Password,
            }, cancellationToken);

            LoginResponsePacket response = await responseReceived.Task
                .WaitAsync(LoginResponseTimeout, cancellationToken);

            if (!response.Success || string.IsNullOrEmpty(response.Token))
                return TokenResult.Fail($"AuthServer 로그인 거부: {credential.Username}");

            long expiresUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (long)_tokenTtl.TotalSeconds;
            _tokenCache[credential.AccountIndex] = new CachedToken(response.Token, expiresUnix);
            return TokenResult.Ok(response.Token);
        }
        catch (OperationCanceledException)
        {
            throw; // 셧다운은 실패 통계가 아니라 정상 취소로 전파
        }
        catch (Exception ex)
        {
            return TokenResult.Fail($"AuthServer 로그인 실패({credential.Username}): {ex.Message}");
        }
    }
}
