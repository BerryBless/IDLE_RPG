using ServerLib.Core.Auth;
using WebClient;

namespace WebClient.Tests;

/// <summary>게스트 토큰 발급기 단위 테스트: 닉네임 정제·계정 ID 대역·토큰 왕복·디렉터리 등록.</summary>
public sealed class GuestTokenIssuerTests
{
    private static readonly byte[] Secret = "webclient-issuer-test-hmac-secret-32b!"u8.ToArray();

    private static (GuestTokenIssuer Issuer, GuestDirectory Directory) Create()
    {
        var directory = new GuestDirectory();
        var issuer = new GuestTokenIssuer(new HmacAuthTokenCodec(Secret), directory, TimeSpan.FromMinutes(10));
        return (issuer, directory);
    }

    [Fact]
    public void 빈_닉네임은_Guest_4자리로_자동_생성된다()
    {
        var (issuer, _) = Create();
        GuestIdentity id = issuer.Issue("   ");
        Assert.Matches(@"^Guest-\d{4}$", id.Nickname);
    }

    [Fact]
    public void null_닉네임도_자동_생성된다()
    {
        var (issuer, _) = Create();
        GuestIdentity id = issuer.Issue(null);
        Assert.Matches(@"^Guest-\d{4}$", id.Nickname);
    }

    [Fact]
    public void 닉네임은_공백제거_후_그대로_쓴다()
    {
        var (issuer, _) = Create();
        GuestIdentity id = issuer.Issue("  용사님  ");
        Assert.Equal("용사님", id.Nickname);
    }

    [Fact]
    public void 제어문자는_제거된다()
    {
        Assert.Equal("ab", GuestTokenIssuer.SanitizeNickname("a\r\n\tb\0", accountId: 1_000_000));
    }

    [Fact]
    public void 길이는_16자로_절단된다()
    {
        string long32 = new string('x', 32);
        Assert.Equal(16, GuestTokenIssuer.SanitizeNickname(long32, 1_000_000).Length);
    }

    [Fact]
    public void 제어문자만_있으면_자동_생성된다()
    {
        Assert.Matches(@"^Guest-\d{4}$", GuestTokenIssuer.SanitizeNickname("\r\n\0", accountId: 1_000_042));
    }

    [Fact]
    public void 계정ID는_게스트_대역에서_시작한다()
    {
        var (issuer, _) = Create();
        GuestIdentity id = issuer.Issue("a");
        Assert.Equal(GuestTokenIssuer.FirstGuestAccountId, id.AccountId);
    }

    [Fact]
    public async Task 동시_발급에도_계정ID는_유일하다()
    {
        var (issuer, _) = Create();
        const int Count = 200;
        GuestIdentity[] issued = await Task.WhenAll(
            Enumerable.Range(0, Count).Select(i => Task.Run(() => issuer.Issue($"n{i}"))));
        Assert.Equal(Count, issued.Select(x => x.AccountId).Distinct().Count());
        Assert.All(issued, x => Assert.True(x.AccountId >= GuestTokenIssuer.FirstGuestAccountId));
    }

    [Fact]
    public void 발급_토큰은_동일_비밀키_코덱으로_검증되고_클레임이_일치한다()
    {
        var (issuer, _) = Create();
        GuestIdentity id = issuer.Issue("검증용사");

        // GameServer SessionAuthGate가 하는 것과 동일한 검증 경로.
        var validator = new HmacAuthTokenCodec(Secret);
        Assert.True(validator.TryValidate(id.Token, out AuthTokenClaims claims));
        Assert.Equal(id.AccountId, claims.AccountId);
        Assert.Equal("검증용사", claims.Username);
        Assert.True(claims.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void 다른_비밀키로는_검증에_실패한다()
    {
        var (issuer, _) = Create();
        GuestIdentity id = issuer.Issue("a");
        var wrongValidator = new HmacAuthTokenCodec("completely-different-secret-value-32b!!"u8.ToArray());
        Assert.False(wrongValidator.TryValidate(id.Token, out _));
    }

    [Fact]
    public void 발급_즉시_디렉터리에_등록된다()
    {
        var (issuer, directory) = Create();
        GuestIdentity id = issuer.Issue("등록확인");
        Assert.True(directory.TryResolveMvp($"player-{id.AccountId}-abc123", out int accountId, out string nickname));
        Assert.Equal(id.AccountId, accountId);
        Assert.Equal("등록확인", nickname);
    }
}
