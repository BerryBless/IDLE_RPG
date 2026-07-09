namespace ServerLib.Core.Auth;

/// <summary>
/// <see cref="IAuthTokenIssuer"/>가 발급한 토큰의 서명·만료를 검증하고 클레임을 복원합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 구현체는 Thread-safe해야 합니다. 검증은 네트워크 IO
/// 스레드(예: GameServer의 <c>OnReceived</c> 콜백)에서 직접 호출될 수 있으므로 내부 상태 변경 없이
/// 순수 함수로 동작해야 합니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 검증 성공 시 <see cref="AuthTokenClaims.Username"/>을
/// 위해 string 힙 할당이 발생합니다. 검증 실패 경로(형식 오류·서명 불일치·만료)는 조기 반환하여
/// 할당을 최소화합니다.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. DB 조회 없이 순수 HMAC 재계산만 수행하는
/// 무상태(stateless) 검증입니다 — 이것이 이 코덱을 고른 이유입니다(토큰 자체에 서명이 있어 검증 측이
/// 발급자와 별도 DB를 공유하지 않아도 됨).</description></item>
/// </list>
/// </remarks>
public interface IAuthTokenValidator
{
    /// <summary>토큰의 서명과 만료 여부를 검증하고, 성공 시 클레임을 복원합니다.</summary>
    /// <param name="token">검증할 토큰 문자열입니다.</param>
    /// <param name="claims">검증 성공 시 복원된 클레임입니다. 실패 시 <c>default</c>입니다.</param>
    /// <returns>서명이 유효하고 아직 만료되지 않았으면 <c>true</c>입니다.</returns>
    bool TryValidate(string token, out AuthTokenClaims claims);
}
