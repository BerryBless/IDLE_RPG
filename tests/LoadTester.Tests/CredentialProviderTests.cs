using LoadTester.Auth;

namespace LoadTester.Tests;

/// <summary><see cref="CredentialProvider"/>의 계정 매핑·시딩 포맷 일치 검증.</summary>
public class CredentialProviderTests
{
    [Fact]
    public void 시딩규칙_포맷과_일치한다()
    {
        var provider = new CredentialProvider(3000);
        var credential = provider.GetFor(0);
        Assert.Equal(0, credential.AccountIndex);
        Assert.Equal("user0000", credential.Username);
        Assert.Equal("Pass!0000", credential.Password);

        var last = provider.GetFor(2999);
        Assert.Equal("user2999", last.Username);
        Assert.Equal("Pass!2999", last.Password);
    }

    [Fact]
    public void 계정수를_넘는_인덱스는_모듈러로_재사용된다()
    {
        var provider = new CredentialProvider(3000);
        Assert.Equal(provider.GetFor(0), provider.GetFor(3000));
        Assert.Equal(provider.GetFor(1234), provider.GetFor(4234));
        Assert.Equal(2999, provider.GetFor(5999).AccountIndex);
    }

    [Fact]
    public void 계정수_0이하_예외()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CredentialProvider(0));
    }
}
