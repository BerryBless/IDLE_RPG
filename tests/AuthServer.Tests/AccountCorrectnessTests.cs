using AuthServer.Login;
using AuthServer.Security;
using AuthServer.Seeding;
using ServerLib.Core.Auth;

namespace AuthServer.Tests;

/// <summary>
/// 더미 계정 3000개를 인메모리 저장소에 시딩한 뒤, 로그인 정확성(맞는 자격증명=성공,
/// 틀린 비밀번호/존재하지 않는 계정=실패)을 검증한다. 목적은 부하/동시성이 아니라 정확성이다.
/// </summary>
public class AccountCorrectnessTests
{
    // 3000건 시딩+검증을 sub-second로 유지하기 위해 낮은 반복 횟수 사용(운영 기본값 100_000 대비).
    // 알고리즘은 동일한 Pbkdf2PasswordHasher이므로 정확성 검증에는 영향 없음.
    private const int FastIterations = 1000;
    private const int SeedCount = 3000;
    private static readonly byte[] TestSecret = "correctness-test-secret"u8.ToArray();

    private static async Task<(LoginService Login, HmacAuthTokenCodec Codec)> SeedAsync()
    {
        var repo = new InMemoryAccountRepository();
        var hasher = new Pbkdf2PasswordHasher(FastIterations);
        var codec = new HmacAuthTokenCodec(TestSecret);
        var login = new LoginService(repo, hasher, codec, TimeSpan.FromMinutes(10));

        await AccountSeeder.SeedAsync(repo, hasher, SeedCount);

        return (login, codec);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1499)]
    [InlineData(2998)]
    [InlineData(2999)]
    public async Task AuthenticateAsync_CorrectCredentials_SucceedsWithValidToken(int index)
    {
        (LoginService login, HmacAuthTokenCodec codec) = await SeedAsync();
        string username = AccountSeeder.UsernameFor(index);
        string password = AccountSeeder.PasswordFor(index);

        LoginResult result = await login.AuthenticateAsync(username, password);

        Assert.True(result.Success);
        Assert.False(string.IsNullOrEmpty(result.Token));
        Assert.True(codec.TryValidate(result.Token, out AuthTokenClaims claims));
        Assert.Equal(index + 1, claims.AccountId);
        Assert.Equal(username, claims.Username);
    }

    [Fact]
    public async Task AuthenticateAsync_AllThreeThousandAccounts_AllSucceedWithCorrectCredentials()
    {
        (LoginService login, _) = await SeedAsync();

        for (int i = 0; i < SeedCount; i++)
        {
            LoginResult result = await login.AuthenticateAsync(
                AccountSeeder.UsernameFor(i), AccountSeeder.PasswordFor(i));

            Assert.True(result.Success, $"index {i} expected success");
        }
    }

    [Fact]
    public async Task AuthenticateAsync_WrongPassword_FailsWithEmptyToken()
    {
        (LoginService login, _) = await SeedAsync();

        LoginResult result = await login.AuthenticateAsync(
            AccountSeeder.UsernameFor(0), "definitely-the-wrong-password");

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Token);
    }

    [Fact]
    public async Task AuthenticateAsync_NonexistentUsername_FailsWithEmptyToken()
    {
        (LoginService login, _) = await SeedAsync();

        LoginResult result = await login.AuthenticateAsync("no-such-user-ever", "irrelevant");

        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Token);
    }
}
