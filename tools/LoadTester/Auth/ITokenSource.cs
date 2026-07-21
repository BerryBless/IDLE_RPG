namespace LoadTester.Auth;

/// <summary>토큰 획득 결과입니다.</summary>
/// <param name="Success">획득 성공 여부.</param>
/// <param name="Token">성공 시 GameServer 인증에 쓸 HMAC 토큰, 실패 시 null.</param>
/// <param name="Error">실패 시 원인 요약, 성공 시 null.</param>
public readonly record struct TokenResult(bool Success, string? Token, string? Error)
{
    /// <summary>성공 결과를 만듭니다.</summary>
    public static TokenResult Ok(string token) => new(true, token, null);

    /// <summary>실패 결과를 만듭니다.</summary>
    public static TokenResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// 가상 클라이언트가 GameServer 인증 게이트를 통과할 HMAC 토큰을 획득하는 전략 인터페이스입니다.
/// game 모드는 <see cref="LocalHmacTokenSource"/>(코덱 직접 발급), full 모드는
/// <see cref="AuthServerTokenSource"/>(실제 AuthServer 로그인)로 구현이 갈린다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 구현체는 Thread-safe해야 한다 — 다수 VirtualClient
/// 태스크가 동시에 호출한다.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking(비동기). full 모드 구현은 네트워크 왕복을
/// 포함할 수 있으므로 호출자는 결과를 기다리는 동안 취소 가능해야 한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 토큰 문자열 등 호출당 소량 할당 허용
/// (재접속 시에만 호출되는 저빈도 경로).</description></item>
/// </list>
/// </remarks>
public interface ITokenSource
{
    /// <summary>지정 클라이언트 인덱스용 토큰을 획득합니다.</summary>
    /// <param name="clientIndex">가상 클라이언트 인덱스(0부터). 내부에서 계정으로 매핑된다.</param>
    /// <param name="cancellationToken">획득 취소 토큰.</param>
    /// <returns>획득 결과. 네트워크 실패 등은 예외 대신 <see cref="TokenResult.Fail"/>로 반환한다.</returns>
    ValueTask<TokenResult> AcquireAsync(int clientIndex, CancellationToken cancellationToken);
}
