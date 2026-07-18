using AuthServer.Accounts;
using AuthServer.Security;
using ServerLib.Core.Auth;

namespace AuthServer.Login;

/// <summary>
/// 자격증명(사용자 이름·비밀번호)을 검증하고, 성공 시 세션 토큰을 발급하는 로그인 유스케이스입니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 생성자 주입 의존성(저장소·해셔·토큰 발급기)이
/// 각각 Thread-safe하고 이 클래스는 자체 가변 상태를 갖지 않으므로 여러 로그인 요청을 동시에
/// 처리할 수 있습니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 저장소 조회 결과(<see cref="Account"/>)와 발급된
/// 토큰 문자열 등 호출당 여러 힙 할당이 발생합니다.</description></item>
/// <item><description><b>Blocking:</b> <see cref="AuthenticateAsync"/> 내부에서 저장소 조회는
/// 비동기 I/O이고, 비밀번호 해시 검증(<see cref="IPasswordHasher.Verify"/>)은 의도적으로 느린
/// 동기 CPU 블로킹 구간(수십 ms급, §IPasswordHasher 참고)이지만 <see cref="Task.Run(Action)"/>으로
/// 스레드 풀 워커에 오프로드해 호출자(네트워크 IO 스레드, <see cref="AuthConnectionHandler.OnReceived"/>)를
/// 점유하지 않습니다 — 코드리뷰 High 발견 수정
/// (<c>docs/code-reviews/2026-07-18-auth-login-and-web-monitoring-review.md</c>): 이전에는
/// <c>OnReceived</c>가 직접 호출하는 이 메서드 안에서 <c>Verify</c>를 동기 실행해, 동시 로그인이
/// 스레드 풀 워커 수를 넘으면 나머지 요청이 대기열에 쌓여 전체 수신 처리량이 붕괴할 수 있었다.
/// </description></item>
/// </list>
/// </remarks>
public sealed class LoginService
{
    private readonly IAccountRepository _repository;
    private readonly IPasswordHasher _hasher;
    private readonly IAuthTokenIssuer _tokenIssuer;
    private readonly TimeSpan _tokenLifetime;

    /// <summary>의존성을 주입하여 로그인 서비스를 생성합니다.</summary>
    /// <param name="repository">계정 조회에 사용할 저장소입니다.</param>
    /// <param name="hasher">비밀번호 검증에 사용할 해셔입니다.</param>
    /// <param name="tokenIssuer">로그인 성공 시 토큰을 발급할 발급기입니다.</param>
    /// <param name="tokenLifetime">발급된 토큰이 유효한 기간입니다.</param>
    public LoginService(
        IAccountRepository repository,
        IPasswordHasher hasher,
        IAuthTokenIssuer tokenIssuer,
        TimeSpan tokenLifetime)
    {
        _repository = repository;
        _hasher = hasher;
        _tokenIssuer = tokenIssuer;
        _tokenLifetime = tokenLifetime;
    }

    /// <summary>사용자 이름과 비밀번호를 검증하고, 성공 시 토큰을 발급합니다.</summary>
    /// <param name="username">로그인 시도에 쓰인 사용자 이름입니다.</param>
    /// <param name="password">로그인 시도에 쓰인 평문 비밀번호입니다.</param>
    /// <param name="ct">취소 토큰입니다.</param>
    /// <returns>계정이 없거나 비밀번호가 틀리면 실패 결과, 일치하면 토큰을 포함한 성공 결과입니다.</returns>
    public async ValueTask<LoginResult> AuthenticateAsync(
        string username, string password, CancellationToken ct = default)
    {
        Account? account = await _repository.FindByUsernameAsync(username, ct);
        if (account is null)
            return LoginResult.Failed();

        // Task.Run: PBKDF2 10만회 반복(수십 ms, 동기 CPU 연산)을 스레드 풀 워커로 넘겨 호출자인
        // 네트워크 IO 스레드(AuthConnectionHandler.OnReceived)를 즉시 반환시킨다 — 그래야 해시 계산이
        // 끝나기 전에도 그 IO 스레드가 다른 세션의 수신을 계속 처리할 수 있다(클래스 remarks 참고).
        bool verified = await Task.Run(() => _hasher.Verify(password, account.PasswordHash), ct);
        if (!verified)
            return LoginResult.Failed();

        string token = _tokenIssuer.Issue(account.AccountId, account.Username, DateTimeOffset.UtcNow + _tokenLifetime);
        return LoginResult.Succeeded(token);
    }
}
