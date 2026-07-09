using System.Security.Cryptography;
using System.Text;

namespace ServerLib.Core.Auth;

/// <summary>
/// HMAC-SHA256 서명 기반 무상태(stateless) 세션 토큰 발급/검증기입니다.
/// AuthServer가 로그인 성공 시 토큰을 발급하고, GameServer가 별도 DB 조회 없이 서명만으로
/// 토큰을 검증할 수 있도록 두 서버가 <c>ServerLib</c>를 통해 이 코덱을 공유합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 생성자 이후 <c>_secret</c>은 불변이며,
/// <see cref="HMACSHA256.HashData(byte[], byte[])"/> 정적 메서드를 사용해 인스턴스를 공유하지 않으므로
/// 여러 스레드(IO 스레드 포함)가 동시에 <see cref="Issue"/>/<see cref="TryValidate"/>를 호출해도 안전합니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 토큰 문자열·payload 바이트 배열·서명 바이트 배열 등
/// 호출당 여러 힙 할당이 발생합니다. 로그인/토큰검증은 저빈도 경로이므로 허용.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. DB 조회 없이 순수 HMAC 재계산 + 문자열 파싱만
/// 수행합니다(수 µs~수십 µs급 CPU 연산).</description></item>
/// </list>
/// </remarks>
/// <remarks>
/// 토큰 포맷: <c>base64url(payload) + "." + base64url(HMAC-SHA256(secret, payload))</c>,
/// <c>payload = "{accountId}|{base64url(username)}|{expiryUnixSeconds}"</c>.
/// username을 payload 내부에서 base64url로 한 번 더 인코딩하는 이유: username에 구분자 '|'가
/// 포함되면(예: "weird|name") 필드 경계가 밀려 잘못 파싱될 수 있으므로, 파싱 전에 구분자 충돌
/// 가능성을 원천 차단한다.
/// </remarks>
public sealed class HmacAuthTokenCodec : IAuthTokenIssuer, IAuthTokenValidator
{
    // 서명/검증에 쓰이는 공유 비밀키. 생성자 이후 절대 변경되지 않아(불변) 여러 스레드가
    // 동기화 없이 안전하게 읽을 수 있다.
    private readonly byte[] _secret;

    /// <summary>공유 비밀키로 코덱을 생성합니다.</summary>
    /// <param name="secret">HMAC 서명에 사용할 비밀키 바이트입니다. 비어 있으면 안 됩니다.</param>
    /// <exception cref="ArgumentException"><paramref name="secret"/>이 null이거나 길이 0일 때</exception>
    public HmacAuthTokenCodec(byte[] secret)
    {
        if (secret is null || secret.Length == 0)
            throw new ArgumentException("secret은 비어 있을 수 없습니다.", nameof(secret));
        // 방어적 복사: 호출자가 이후 원본 배열을 변형해도 코덱 내부 상태가 오염되지 않도록 분리 보관
        _secret = (byte[])secret.Clone();
    }

    /// <inheritdoc/>
    public string Issue(int accountId, string username, DateTimeOffset expiresAt)
    {
        string encodedUsername = Base64UrlEncode(Encoding.UTF8.GetBytes(username));
        string payload = $"{accountId}|{encodedUsername}|{expiresAt.ToUnixTimeSeconds()}";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

        byte[] signature = HMACSHA256.HashData(_secret, payloadBytes);

        return Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(signature);
    }

    /// <inheritdoc/>
    public bool TryValidate(string token, out AuthTokenClaims claims)
    {
        claims = default;

        int dotIndex = token.IndexOf('.');
        if (dotIndex < 0 || dotIndex == token.Length - 1)
            return false;

        string payloadSegment = token[..dotIndex];
        string signatureSegment = token[(dotIndex + 1)..];

        byte[] payloadBytes;
        byte[] receivedSignature;
        try
        {
            payloadBytes = Base64UrlDecode(payloadSegment);
            receivedSignature = Base64UrlDecode(signatureSegment);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] expectedSignature = HMACSHA256.HashData(_secret, payloadBytes);
        // CryptographicOperations.FixedTimeEquals: 비교 소요 시간이 불일치 위치에 의존하지 않는
        // 상수 시간 비교. 일반 SequenceEqual/==은 최초 불일치 바이트에서 조기 종료해 타이밍
        // 사이드채널로 서명을 바이트 단위로 역추적당할 수 있어 위험하다.
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, receivedSignature))
            return false;

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(payloadBytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        string[] parts = payload.Split('|');
        if (parts.Length != 3)
            return false;

        if (!int.TryParse(parts[0], out int accountId))
            return false;

        string username;
        try
        {
            username = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        }
        catch (FormatException)
        {
            return false;
        }

        if (!long.TryParse(parts[2], out long expirySeconds))
            return false;

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expirySeconds);
        if (expiresAt <= DateTimeOffset.UtcNow)
            return false;

        claims = new AuthTokenClaims(accountId, username, expiresAt);
        return true;
    }

    // 표준 Base64는 '+', '/', '=' 문자를 쓰는데, 이들은 토큰이 URL 쿼리·헤더 값으로 쓰일 때
    // 추가 이스케이프가 필요해 부적합하다. '-', '_'로 치환하고 패딩 '='을 제거하는
    // URL-safe 변형(RFC 4648 §5)을 직접 구현해 표준 라이브러리 하나 추가 없이 해결한다.
    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static byte[] Base64UrlDecode(string value)
    {
        string base64 = value.Replace('-', '+').Replace('_', '/');
        int padding = (4 - base64.Length % 4) % 4;
        return Convert.FromBase64String(base64 + new string('=', padding));
    }
}
