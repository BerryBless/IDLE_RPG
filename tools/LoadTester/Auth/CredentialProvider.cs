namespace LoadTester.Auth;

/// <summary>
/// 클라이언트 인덱스를 시딩 계정에 모듈러 매핑합니다(10,000+ 클라이언트가 3,000계정을 재사용).
/// 계정명/비밀번호 포맷은 AuthServer의 <c>AccountSeeder.UsernameFor/PasswordFor</c>와 동일한
/// <c>user{i:D4}</c>/<c>Pass!{i:D4}</c> 규칙을 로컬로 재정의한다 — LoadTester는 ServerLib만
/// 참조하므로 AuthServer 코드를 가져올 수 없어 포맷 문자열 결합을 의도적으로 감수한다
/// (시딩 규칙 변경 시 이 클래스도 함께 갱신할 것).
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe(무상태 순수 계산). <b>[Memory Allocation:]</b> 호출당
/// 문자열 2개(보간). <b>[Blocking:]</b> Non-blocking.
/// </remarks>
public sealed class CredentialProvider
{
    private readonly int _accountCount;

    /// <summary>재사용할 시딩 계정 수로 프로바이더를 생성합니다.</summary>
    /// <param name="accountCount">계정 수(1 이상).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="accountCount"/>가 1 미만일 때.</exception>
    public CredentialProvider(int accountCount)
    {
        if (accountCount < 1)
            throw new ArgumentOutOfRangeException(nameof(accountCount), accountCount, "계정 수는 1 이상이어야 합니다.");
        _accountCount = accountCount;
    }

    /// <summary>클라이언트 인덱스에 대응하는 계정 자격 증명을 반환합니다.</summary>
    /// <param name="clientIndex">가상 클라이언트 인덱스(0부터).</param>
    public Credential GetFor(int clientIndex)
    {
        int accountIndex = clientIndex % _accountCount;
        return new Credential(accountIndex, $"user{accountIndex:D4}", $"Pass!{accountIndex:D4}");
    }
}
