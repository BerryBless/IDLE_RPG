using System.Text;

namespace ServerLib.Core.Auth;

/// <summary>
/// HMAC 공유 비밀키를 여러 프로세스(AuthServer 발급·GameServer 검증·LoadTester 부하)가 동일한 정책으로
/// 해석하기 위한 단일 소스 리졸버입니다. 정책: <c>IDLERPG_AUTH_HMAC_SECRET</c> 환경 변수 우선 →
/// 없으면 <c>DEBUG</c> 빌드에서만 개발용 폴백 → 어떤 경로든 <see cref="MinSecretBytes"/> 미만이면 거부.
/// </summary>
/// <remarks>
/// <b>[설계 배경]</b>
/// <list type="bullet">
/// <item><description>이전에는 이 정책과 개발용 폴백 상수가 4곳(AuthServer/GameServer/LoadTester×2)에 복제돼
/// 시크릿 정책 변경 시 동시 수정을 요구하고 하나라도 누락되면 발급/검증 불일치로 조용히 인증이 깨졌다
/// (코드리뷰 Medium, <c>docs/code-reviews/2026-07-22-loadtest-stress-hardening-review.md</c>). 이 타입이 유일한 정본이다.</description></item>
/// <item><description><b>개발용 폴백은 <c>DEBUG</c>로만 컴파일된다.</b> Release 빌드의 <c>ServerLib.dll</c>에는
/// 그 문자열이 남지 않아(공개 저장소 노출 값이 프로덕션 바이너리에 실리지 않음), env 미설정 시
/// <see cref="TryResolve"/>가 <see langword="false"/>를 돌려준다 — 호출부가 throw할지 종료할지 결정한다.</description></item>
/// <item><description><b>Thread Safety:</b> Thread-safe. 순수 함수로 공유 상태를 두지 않으며 환경 변수만 읽는다.</description></item>
/// <item><description><b>Memory Allocation:</b> 성공 시 비밀키 바이트 배열 1개만 할당(UTF-8 인코딩). 실패 시 무할당.</description></item>
/// <item><description><b>Blocking:</b> 즉시 반환(동기, 논블로킹).</description></item>
/// </list>
/// </remarks>
public static class HmacSecretResolver
{
    /// <summary>공유 비밀키를 담는 환경 변수 이름입니다.</summary>
    public const string EnvVarName = "IDLERPG_AUTH_HMAC_SECRET";

    /// <summary>HMAC-SHA256 키 최소 길이(바이트)입니다. NIST SP 800-107: 키는 해시 출력 크기
    /// (SHA-256=32바이트) 이상의 엔트로피를 가져야 안전합니다.</summary>
    public const int MinSecretBytes = 32;

    /// <summary><see cref="TryResolve"/>가 비밀키를 어디서 얻었는지 나타냅니다.</summary>
    public enum SecretSource
    {
        /// <summary>환경 변수에서 해석했습니다.</summary>
        Environment,
        /// <summary>환경 변수가 없어 <c>DEBUG</c> 빌드의 개발용 폴백을 사용했습니다(호출부가 경고를 출력해야 함).</summary>
        DevFallback,
    }

    /// <summary>공유 HMAC 비밀키를 정책에 따라 해석합니다.</summary>
    /// <param name="secret">성공 시 비밀키 바이트(UTF-8). 실패 시 빈 배열.</param>
    /// <param name="source">성공 시 비밀키 출처. <see cref="SecretSource.DevFallback"/>이면 호출부가 개발용 키 사용 경고를 출력해야 합니다.</param>
    /// <param name="error">실패 시 사람이 읽을 수 있는 사유(호출부가 throw 또는 stderr 출력에 사용). 성공 시 <see langword="null"/>.</param>
    /// <returns>해석 성공 여부. Release 빌드에서 env가 없거나, 어떤 경로든 <see cref="MinSecretBytes"/> 미만이면 <see langword="false"/>.</returns>
    /// <remarks>
    /// 호출부는 정책 차이를 반환값 처리로만 표현합니다 — 서버(AuthServer/GameServer)는 <see langword="false"/>에
    /// <see cref="InvalidOperationException"/>으로 fail-fast하고, 부하 툴(LoadTester)은 stderr 출력 후 종료 코드로 실패합니다.
    /// </remarks>
    public static bool TryResolve(out byte[] secret, out SecretSource source, out string? error)
    {
        secret = [];
        source = SecretSource.Environment;
        error = null;

        string? fromEnv = System.Environment.GetEnvironmentVariable(EnvVarName);
        string secretText;
        if (fromEnv is not null)
        {
            secretText = fromEnv;
            source = SecretSource.Environment;
        }
        else
        {
#if DEBUG
            // DEBUG 전용 폴백: 로컬 dotnet run 편의용. Release 빌드에서는 #else로 완전 배제되어
            // 이 문자열이 어셈블리에 남지 않는다(컴파일 시점 제거).
            secretText = "dev-only-insecure-hmac-secret-change-me";
            source = SecretSource.DevFallback;
#else
            error =
                $"{EnvVarName} 환경 변수가 설정되지 않았습니다. Release 빌드에서는 개발용 기본 비밀키를 사용할 수 없습니다 — " +
                "해당 문자열은 공개 저장소 소스에 노출되어 있어, 그 값을 그대로 쓰면 공격자가 유효 토큰을 위조해 " +
                "GameServer 인증 게이트를 완전히 우회할 수 있습니다. 배포 전 이 환경 변수를 32바이트 이상의 고엔트로피 값으로 설정하세요.";
            return false;
#endif
        }

        byte[] bytes = Encoding.UTF8.GetBytes(secretText);
        if (bytes.Length < MinSecretBytes)
        {
            error =
                $"{EnvVarName}이 너무 짧습니다({bytes.Length}바이트, 최소 {MinSecretBytes}바이트 필요). " +
                "HMAC-SHA256 키는 해시 출력 크기 이상의 엔트로피를 가져야 안전합니다.";
            return false;
        }

        secret = bytes;
        return true;
    }
}
