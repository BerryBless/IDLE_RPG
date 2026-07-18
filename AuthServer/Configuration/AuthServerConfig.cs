using System.Text;

namespace AuthServer.Configuration;

/// <summary>
/// AuthServer 실행 설정입니다. 프로젝트 전반의 관례(경량 설정, config 프레임워크 없음)를 따라
/// 환경 변수 + 하드코딩 기본값으로만 구성합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe. 모든 값이 <c>static readonly</c>로 프로세스 시작 시 1회
/// 평가된 뒤 불변입니다.
/// <b>[Memory Allocation:]</b> 문자열/바이트 배열이 프로세스 수명 동안 1회 할당되어 유지됩니다.
/// </remarks>
public static class AuthServerConfig
{
    /// <summary>MongoDB 연결 문자열입니다. <c>IDLERPG_MONGO_CONN</c> 환경 변수, 없으면 로컬 기본값.</summary>
    public static string MongoConnectionString { get; } =
        Environment.GetEnvironmentVariable("IDLERPG_MONGO_CONN") ?? "mongodb://localhost:27017";

    /// <summary>사용할 MongoDB 데이터베이스 이름입니다. <c>IDLERPG_MONGO_DB</c> 환경 변수, 없으면 <c>idlerpg</c>.</summary>
    public static string MongoDatabaseName { get; } =
        Environment.GetEnvironmentVariable("IDLERPG_MONGO_DB") ?? "idlerpg";

    /// <summary>리스닝 포트입니다. <c>IDLERPG_AUTH_PORT</c> 환경 변수, 없으면 7778
    /// (GameServer 7777, EchoServer 9000과 겹치지 않도록 구분).</summary>
    public static int Port { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("IDLERPG_AUTH_PORT"), out int port) ? port : 7778;

    /// <summary>HMAC-SHA256 키는 해시 출력 크기(32바이트) 이상의 엔트로피를 가져야 한다는
    /// NIST SP 800-107 권고를 따른 최소 길이입니다.</summary>
    private const int MinHmacSecretBytes = 32;

    /// <summary>HMAC 토큰 서명에 쓰이는 공유 비밀키 바이트입니다.</summary>
    /// <remarks>
    /// <c>IDLERPG_AUTH_HMAC_SECRET</c> 환경 변수가 없으면 <c>DEBUG</c> 빌드에서만 개발용 기본값을
    /// 쓰고(<see cref="IsUsingDevHmacSecret"/>로 그 사실을 알 수 있게 해 <c>Program.cs</c>가 시작 시
    /// 경고를 출력한다), Release 빌드에서는 즉시 <see cref="InvalidOperationException"/>으로 기동을
    /// 중단한다(fail-fast) — 코드리뷰 Critical 발견 수정
    /// (<c>docs/code-reviews/2026-07-18-auth-login-and-web-monitoring-review.md</c>): 이전에는
    /// 환경 변수 누락 시 소스에 하드코딩된(= 공개 저장소에 노출된) 개발용 키로 조용히 폴백해,
    /// 운영 배포에서 설정을 빠뜨리면 그 알려진 키로 누구나 토큰을 위조해 인증을 완전히 우회할 수
    /// 있었다. 환경 변수가 있어도 <see cref="MinHmacSecretBytes"/> 미만이면(추측하기 쉬운 값)
    /// 마찬가지로 즉시 실패한다. GameServer가 <see cref="ServerLib.Core.Auth.HmacAuthTokenCodec"/>로
    /// 토큰을 검증하려면 반드시 이 값을 동일하게 공유해야 한다.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Release 빌드에서 환경 변수가 없거나, 값이 <see cref="MinHmacSecretBytes"/>바이트 미만일 때
    /// (타입 초기화 시점에 즉시 발생 — 이 클래스의 어떤 정적 멤버에 처음 접근하는 순간
    /// <see cref="TypeInitializationException"/>으로 감싸져 전파된다).
    /// </exception>
    public static byte[] HmacSecret { get; } = ResolveHmacSecret();

    /// <summary><see cref="HmacSecret"/>이 환경 변수 대신 개발용 기본값으로 채워졌는지 여부입니다.</summary>
    public static bool IsUsingDevHmacSecret { get; } =
        Environment.GetEnvironmentVariable("IDLERPG_AUTH_HMAC_SECRET") is null;

    private static byte[] ResolveHmacSecret()
    {
        string? fromEnv = Environment.GetEnvironmentVariable("IDLERPG_AUTH_HMAC_SECRET");
        string secretText;
        if (fromEnv is not null)
        {
            secretText = fromEnv;
        }
        else
        {
#if DEBUG
            // DEBUG 전용 폴백: 로컬 dotnet run 편의를 위해 유지하되, Release 빌드에서는 아래 #else로
            // 완전히 배제된다(컴파일 시점에 코드 자체가 어셈블리에 남지 않음).
            secretText = "dev-only-insecure-hmac-secret-change-me";
#else
            throw new InvalidOperationException(
                "IDLERPG_AUTH_HMAC_SECRET 환경 변수가 설정되지 않았습니다. Release 빌드에서는 개발용 기본 비밀키를 " +
                "사용할 수 없습니다 — 해당 문자열은 공개 저장소 소스에 그대로 노출되어 있어, 그 값을 그대로 쓰면 " +
                "공격자가 유효 토큰을 위조해 GameServer 인증 게이트를 완전히 우회할 수 있습니다. " +
                "배포 전 이 환경 변수를 32바이트 이상의 고엔트로피 값으로 설정하세요.");
#endif
        }

        byte[] secret = Encoding.UTF8.GetBytes(secretText);
        if (secret.Length < MinHmacSecretBytes)
        {
            throw new InvalidOperationException(
                $"IDLERPG_AUTH_HMAC_SECRET이 너무 짧습니다({secret.Length}바이트, 최소 {MinHmacSecretBytes}바이트 " +
                "필요). HMAC-SHA256 키는 해시 출력 크기 이상의 엔트로피를 가져야 안전합니다.");
        }
        return secret;
    }

    /// <summary>발급된 로그인 토큰의 유효 기간입니다.</summary>
    public static TimeSpan TokenLifetime { get; } = TimeSpan.FromHours(1);
}
