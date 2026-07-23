using LoadTester.Auth;
using ServerLib.Core.Auth;

namespace LoadTester.Tests;

/// <summary><see cref="LocalHmacTokenSource"/> 발급 토큰이 동일 비밀키 코덱 검증을 통과하는지 확인.</summary>
public class LocalHmacTokenSourceTests
{
    private static readonly byte[] Secret = "loadtester-local-token-test-secret"u8.ToArray();

    [Fact]
    public async Task 발급토큰_동일키코덱_검증통과_클레임일치()
    {
        var codec = new HmacAuthTokenCodec(Secret);
        var source = new LocalHmacTokenSource(codec, new CredentialProvider(3000), TimeSpan.FromHours(1));

        TokenResult result = await source.AcquireAsync(clientIndex: 42, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.Token);
        Assert.True(codec.TryValidate(result.Token!, out var claims));
        Assert.Equal(42, claims.AccountId);
        Assert.Equal("user0042", claims.Username);
        Assert.True(claims.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task 계정재사용_모듈러매핑된_계정으로_발급된다()
    {
        var codec = new HmacAuthTokenCodec(Secret);
        var source = new LocalHmacTokenSource(codec, new CredentialProvider(3000), TimeSpan.FromHours(1));

        TokenResult result = await source.AcquireAsync(clientIndex: 3005, CancellationToken.None);

        Assert.True(codec.TryValidate(result.Token!, out var claims));
        Assert.Equal(5, claims.AccountId);
        Assert.Equal("user0005", claims.Username);
    }

    [Fact]
    public async Task 다른키코덱_검증실패()
    {
        var issuer = new HmacAuthTokenCodec(Secret);
        var wrongValidator = new HmacAuthTokenCodec("completely-different-secret-key"u8.ToArray());
        var source = new LocalHmacTokenSource(issuer, new CredentialProvider(10), TimeSpan.FromHours(1));

        TokenResult result = await source.AcquireAsync(0, CancellationToken.None);

        Assert.False(wrongValidator.TryValidate(result.Token!, out _));
    }
}
