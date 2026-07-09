namespace ServerLib.Core.Auth;

/// <summary>
/// 계정 인증 성공 후 제시할 수 있는 세션 토큰을 발급합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 구현체는 Thread-safe해야 합니다(여러 로그인 요청이
/// 동시에 같은 인스턴스로 토큰을 발급할 수 있음). 구현이 불변 상태만 참조한다면(예: 서명 비밀키)
/// 별도 동기화 없이 안전합니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 반환되는 토큰 문자열 및 내부 인코딩 과정에서
/// 힙 할당이 발생합니다(빈도가 낮은 로그인 경로이므로 허용 범위).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 순수 CPU 연산(해시/인코딩)만 수행하며
/// I/O를 수행하지 않습니다.</description></item>
/// </list>
/// </remarks>
public interface IAuthTokenIssuer
{
    /// <summary>지정한 계정 정보로 서명된 토큰을 발급합니다.</summary>
    /// <param name="accountId">토큰이 가리킬 계정의 고유 식별자입니다.</param>
    /// <param name="username">토큰에 포함할 사용자 이름입니다.</param>
    /// <param name="expiresAt">토큰 만료 시각입니다.</param>
    /// <returns>서명이 포함된 토큰 문자열입니다.</returns>
    string Issue(int accountId, string username, DateTimeOffset expiresAt);
}
