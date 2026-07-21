using ServerLib.Core.Auth;

namespace LoadTester.Auth;

/// <summary>
/// game 모드용 토큰 소스: AuthServer를 거치지 않고 <see cref="HmacAuthTokenCodec"/>으로
/// 직접 토큰을 발급합니다. GameServer와 동일한 비밀키를 공유해야 검증을 통과한다
/// (env <c>IDLERPG_AUTH_HMAC_SECRET</c>, DEBUG 빌드 GameServer는 개발용 기본키 폴백).
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe — <see cref="HmacAuthTokenCodec"/>가
/// 정적 HMAC 연산만 쓰는 불변 객체이고 <see cref="CredentialProvider"/>도 무상태다.</description></item>
/// <item><description><b>Memory Allocation:</b> 발급마다 토큰 문자열 등 소량 할당(저빈도 경로).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 네트워크 왕복 없이 순수 CPU 연산(수십 µs)으로
/// 동기 완료한다 — 항상 완료된 ValueTask를 반환.</description></item>
/// </list>
/// </remarks>
public sealed class LocalHmacTokenSource : ITokenSource
{
    private readonly HmacAuthTokenCodec _codec;
    private readonly CredentialProvider _credentials;
    private readonly TimeSpan _tokenTtl;

    /// <summary>토큰 소스를 생성합니다.</summary>
    /// <param name="codec">GameServer와 동일 비밀키로 생성된 코덱.</param>
    /// <param name="credentials">클라이언트→계정 매핑.</param>
    /// <param name="tokenTtl">발급 토큰 유효기간.</param>
    public LocalHmacTokenSource(HmacAuthTokenCodec codec, CredentialProvider credentials, TimeSpan tokenTtl)
    {
        _codec = codec;
        _credentials = credentials;
        _tokenTtl = tokenTtl;
    }

    /// <inheritdoc/>
    public ValueTask<TokenResult> AcquireAsync(int clientIndex, CancellationToken cancellationToken)
    {
        Credential credential = _credentials.GetFor(clientIndex);
        string token = _codec.Issue(credential.AccountIndex, credential.Username, DateTimeOffset.UtcNow + _tokenTtl);
        return ValueTask.FromResult(TokenResult.Ok(token));
    }
}
