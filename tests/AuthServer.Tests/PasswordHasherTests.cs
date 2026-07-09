using AuthServer.Security;

namespace AuthServer.Tests;

/// <summary>
/// <see cref="Pbkdf2PasswordHasher"/>의 해시/검증 왕복, 오답 거부, salt 무작위성,
/// 변조 감지를 검증한다.
/// </summary>
public class PasswordHasherTests
{
    // 반복 횟수를 낮춰(운영 기본값 100_000 대비) 테스트 실행 시간을 sub-second로 유지한다.
    // 알고리즘 자체는 동일한 Pbkdf2PasswordHasher이므로 로직 검증에는 영향이 없다.
    private const int FastIterations = 1000;

    [Fact]
    public void Hash_ThenVerify_SamePassword_ReturnsTrue()
    {
        var hasher = new Pbkdf2PasswordHasher(FastIterations);

        string encoded = hasher.Hash("correct-horse-battery-staple");
        bool ok = hasher.Verify("correct-horse-battery-staple", encoded);

        Assert.True(ok);
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hasher = new Pbkdf2PasswordHasher(FastIterations);

        string encoded = hasher.Hash("correct-horse-battery-staple");
        bool ok = hasher.Verify("wrong-password", encoded);

        Assert.False(ok);
    }

    [Fact]
    public void Hash_SamePasswordTwice_ProducesDifferentEncodedStrings()
    {
        var hasher = new Pbkdf2PasswordHasher(FastIterations);

        string first = hasher.Hash("same-password");
        string second = hasher.Hash("same-password");

        // 매 호출마다 새 랜덤 salt를 생성하므로 같은 비밀번호라도 인코딩 결과가 달라야 한다.
        Assert.NotEqual(first, second);
        // 그럼에도 둘 다 원래 비밀번호로 검증에 성공해야 한다.
        Assert.True(hasher.Verify("same-password", first));
        Assert.True(hasher.Verify("same-password", second));
    }

    [Fact]
    public void Verify_TamperedEncodedHash_ReturnsFalse()
    {
        var hasher = new Pbkdf2PasswordHasher(FastIterations);
        string encoded = hasher.Hash("correct-horse-battery-staple");

        // 인코딩 문자열 마지막 문자를 변조해 해시 바이트를 훼손한다.
        char lastChar = encoded[^1];
        char flipped = lastChar == '0' ? '1' : '0';
        string tampered = encoded[..^1] + flipped;

        bool ok = hasher.Verify("correct-horse-battery-staple", tampered);

        Assert.False(ok);
    }

    [Fact]
    public void Verify_MalformedEncodedHash_ReturnsFalse()
    {
        var hasher = new Pbkdf2PasswordHasher(FastIterations);

        bool ok = hasher.Verify("anything", "not-a-valid-encoded-hash");

        Assert.False(ok);
    }

    [Fact]
    public void PasswordHasher_DefaultIterations_RoundTrips()
    {
        // 운영 기본값(100_000)이 실제로 올바르게 왕복하는지 별도로 1회 검증한다.
        var hasher = new Pbkdf2PasswordHasher();

        string encoded = hasher.Hash("production-strength-password");
        bool ok = hasher.Verify("production-strength-password", encoded);

        Assert.True(ok);
    }
}
