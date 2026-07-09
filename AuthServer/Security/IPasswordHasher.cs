namespace AuthServer.Security;

/// <summary>
/// 비밀번호를 단방향 해시로 인코딩하고, 평문 비밀번호가 인코딩된 해시와 일치하는지 검증합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 구현체는 Thread-safe해야 합니다(동시 로그인/회원가입
/// 요청이 같은 인스턴스를 공유). 내부 상태를 변경하지 않는 순수 계산이면 안전합니다.</description></item>
/// <item><description><b>Memory Allocation:</b> salt·파생 키·인코딩 문자열 등 호출당 여러 힙 할당이
/// 발생합니다. 로그인/가입은 저빈도 경로이므로 허용 범위입니다.</description></item>
/// <item><description><b>Blocking:</b> <b>의도적으로 CPU 집약적인 동기 블로킹</b> 연산입니다(수십 ms급).
/// 무차별 대입 공격 비용을 높이기 위한 설계이므로, 네트워크 IO 스레드에서 고빈도로 호출한다면
/// 오프로드(Task.Run 등)를 고려해야 합니다.</description></item>
/// </list>
/// </remarks>
public interface IPasswordHasher
{
    /// <summary>평문 비밀번호를 해시하여 저장 가능한 인코딩 문자열로 반환합니다.</summary>
    /// <param name="password">해시할 평문 비밀번호입니다.</param>
    /// <returns>알고리즘 파라미터·salt·해시가 모두 포함된 자기 서술적(self-describing) 인코딩 문자열입니다.</returns>
    string Hash(string password);

    /// <summary>평문 비밀번호가 인코딩된 해시와 일치하는지 검증합니다.</summary>
    /// <param name="password">검증할 평문 비밀번호입니다.</param>
    /// <param name="encodedHash"><see cref="Hash"/>가 생성한 인코딩 문자열입니다.</param>
    /// <returns>비밀번호가 일치하면 <c>true</c>입니다. <paramref name="encodedHash"/>가 형식에 맞지
    /// 않으면 예외를 던지지 않고 <c>false</c>를 반환합니다.</returns>
    bool Verify(string password, string encodedHash);
}
