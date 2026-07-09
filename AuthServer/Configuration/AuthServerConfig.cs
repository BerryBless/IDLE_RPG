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

    /// <summary>HMAC 토큰 서명에 쓰이는 공유 비밀키 바이트입니다.</summary>
    /// <remarks>
    /// <c>IDLERPG_AUTH_HMAC_SECRET</c> 환경 변수가 없으면 개발용 기본값을 쓰되, <see cref="IsUsingDevHmacSecret"/>로
    /// 그 사실을 알 수 있게 해 <c>Program.cs</c>가 시작 시 경고를 출력할 수 있게 한다. 다음 사이클에서
    /// GameServer가 <see cref="ServerLib.Core.Auth.HmacAuthTokenCodec"/>로 토큰을 검증하려면 반드시
    /// 이 값을 동일하게 공유해야 한다.
    /// </remarks>
    public static byte[] HmacSecret { get; } = Encoding.UTF8.GetBytes(
        Environment.GetEnvironmentVariable("IDLERPG_AUTH_HMAC_SECRET") ?? "dev-only-insecure-hmac-secret-change-me");

    /// <summary><see cref="HmacSecret"/>이 환경 변수 대신 개발용 기본값으로 채워졌는지 여부입니다.</summary>
    public static bool IsUsingDevHmacSecret { get; } =
        Environment.GetEnvironmentVariable("IDLERPG_AUTH_HMAC_SECRET") is null;

    /// <summary>발급된 로그인 토큰의 유효 기간입니다.</summary>
    public static TimeSpan TokenLifetime { get; } = TimeSpan.FromHours(1);
}
