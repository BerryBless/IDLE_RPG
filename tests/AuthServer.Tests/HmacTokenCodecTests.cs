using ServerLib.Core.Auth;

namespace AuthServer.Tests;

/// <summary>
/// <see cref="HmacAuthTokenCodec"/>의 발급/검증 왕복과 위변조·만료·비밀키 불일치 거부를 검증한다.
/// </summary>
public class HmacTokenCodecTests
{
    private static readonly byte[] Secret = "test-secret-0123456789"u8.ToArray();

    [Fact]
    public void Issue_ThenValidate_ReturnsOriginalClaims()
    {
        var codec = new HmacAuthTokenCodec(Secret);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(10);

        string token = codec.Issue(accountId: 42, username: "gihyeon", expiresAt);
        bool ok = codec.TryValidate(token, out AuthTokenClaims claims);

        Assert.True(ok);
        Assert.Equal(42, claims.AccountId);
        Assert.Equal("gihyeon", claims.Username);
        Assert.Equal(expiresAt.ToUnixTimeSeconds(), claims.ExpiresAt.ToUnixTimeSeconds());
    }

    [Fact]
    public void TryValidate_TamperedPayload_ReturnsFalse()
    {
        var codec = new HmacAuthTokenCodec(Secret);
        string token = codec.Issue(1, "user0001", DateTimeOffset.UtcNow.AddMinutes(10));

        // payload(첫 세그먼트)만 다른 유효 토큰의 것으로 바꿔치기 -> 서명 불일치 유발
        string otherToken = codec.Issue(2, "user0002", DateTimeOffset.UtcNow.AddMinutes(10));
        string[] tokenParts = token.Split('.');
        string[] otherParts = otherToken.Split('.');
        string tamperedToken = otherParts[0] + "." + tokenParts[1];

        bool ok = codec.TryValidate(tamperedToken, out AuthTokenClaims claims);

        Assert.False(ok);
        Assert.Equal(default, claims);
    }

    [Fact]
    public void TryValidate_TamperedSignature_ReturnsFalse()
    {
        var codec = new HmacAuthTokenCodec(Secret);
        string token = codec.Issue(1, "user0001", DateTimeOffset.UtcNow.AddMinutes(10));
        string[] parts = token.Split('.');
        // 서명 세그먼트의 '첫' 문자를 뒤집어 위조한다. 마지막 문자는 base64url 그룹의 패딩 비트에
        // 걸쳐 있어 값을 바꿔도 디코딩 결과가 우연히 같을 수 있으므로(패딩 비트는 버려짐), 항상
        // 실제 데이터 비트에 영향을 주는 첫 문자를 변조 대상으로 선택한다.
        char firstChar = parts[1][0];
        char flipped = firstChar == 'A' ? 'B' : 'A';
        string tamperedSignature = flipped + parts[1][1..];
        string tamperedToken = parts[0] + "." + tamperedSignature;

        bool ok = codec.TryValidate(tamperedToken, out AuthTokenClaims claims);

        Assert.False(ok);
        Assert.Equal(default, claims);
    }

    [Fact]
    public void TryValidate_ExpiredToken_ReturnsFalse()
    {
        var codec = new HmacAuthTokenCodec(Secret);
        string token = codec.Issue(1, "user0001", DateTimeOffset.UtcNow.AddSeconds(-1));

        bool ok = codec.TryValidate(token, out AuthTokenClaims claims);

        Assert.False(ok);
        Assert.Equal(default, claims);
    }

    [Fact]
    public void TryValidate_WrongSecret_ReturnsFalse()
    {
        var issuer = new HmacAuthTokenCodec(Secret);
        var validator = new HmacAuthTokenCodec("different-secret-abcdef"u8.ToArray());
        string token = issuer.Issue(1, "user0001", DateTimeOffset.UtcNow.AddMinutes(10));

        bool ok = validator.TryValidate(token, out AuthTokenClaims claims);

        Assert.False(ok);
        Assert.Equal(default, claims);
    }

    [Fact]
    public void Issue_ThenValidate_UsernameContainingPipe_RoundTripsCorrectly()
    {
        var codec = new HmacAuthTokenCodec(Secret);
        const string usernameWithPipe = "weird|name|here";

        string token = codec.Issue(7, usernameWithPipe, DateTimeOffset.UtcNow.AddMinutes(10));
        bool ok = codec.TryValidate(token, out AuthTokenClaims claims);

        Assert.True(ok);
        Assert.Equal(usernameWithPipe, claims.Username);
    }

    [Fact]
    public void TryValidate_GarbageString_ReturnsFalse()
    {
        var codec = new HmacAuthTokenCodec(Secret);

        bool ok = codec.TryValidate("not-a-valid-token", out AuthTokenClaims claims);

        Assert.False(ok);
        Assert.Equal(default, claims);
    }
}
