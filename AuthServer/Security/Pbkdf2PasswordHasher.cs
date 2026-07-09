using System.Security.Cryptography;

namespace AuthServer.Security;

/// <summary>
/// <see cref="Rfc2898DeriveBytes"/>(PBKDF2-HMAC-SHA256) 기반 <see cref="IPasswordHasher"/> 구현체입니다.
/// .NET 내장 API만 사용해 외부 NuGet 의존성(BCrypt.Net-Next 등) 없이 비밀번호를 해시합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <c>_iterations</c>는 생성자 이후 불변이며,
/// <see cref="Hash"/>/<see cref="Verify"/> 모두 호출마다 새 <see cref="Rfc2898DeriveBytes"/> 인스턴스를
/// 지역 변수로 생성해 공유 상태가 없습니다.</description></item>
/// <item><description><b>Memory Allocation:</b> salt(16B)·파생 키(32B)·Base64 인코딩 문자열 등 호출당
/// 여러 힙 할당이 발생합니다.</description></item>
/// <item><description><b>Blocking:</b> 동기 CPU 블로킹. 반복 횟수(<c>_iterations</c>)에 비례해 수 ms~
/// 수십 ms가 소요됩니다 — 무차별 대입 공격 비용을 높이기 위해 의도적으로 느립니다.</description></item>
/// </list>
/// </remarks>
/// <remarks>
/// 인코딩 포맷: <c>"{iterations}.{saltBase64}.{hashBase64}"</c>. 반복 횟수를 문자열에 함께 저장해
/// 두어(자기 서술적 포맷) 향후 기본 반복 횟수를 올려도 기존에 저장된 해시를 그대로 검증할 수 있다.
/// </remarks>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int SaltSizeBytes = 16;
    private const int KeySizeBytes = 32;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    // 생성자 이후 불변 — Hash/Verify가 매 호출 새 Rfc2898DeriveBytes를 만들 때 이 값만 참조하므로
    // 여러 스레드가 동시에 호출해도 안전.
    private readonly int _iterations;

    /// <summary>반복 횟수를 지정하여 해셔를 생성합니다.</summary>
    /// <param name="iterations">PBKDF2 반복 횟수입니다. 기본값(100,000)은 운영 강도이며,
    /// 테스트에서는 낮은 값(예: 1,000)을 주입해 대량 데이터 처리 속도를 확보할 수 있습니다.</param>
    public Pbkdf2PasswordHasher(int iterations = 100_000)
    {
        if (iterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(iterations), "iterations는 1 이상이어야 합니다.");
        _iterations = iterations;
    }

    /// <inheritdoc/>
    public string Hash(string password)
    {
        // RandomNumberGenerator.GetBytes: 암호학적으로 안전한 난수 소스(CSPRNG)에서 salt를 추출.
        // Random/Random.Shared는 예측 가능해 salt 용도로 부적합.
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, _iterations, Algorithm, KeySizeBytes);

        return $"{_iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    /// <inheritdoc/>
    public bool Verify(string password, string encodedHash)
    {
        string[] parts = encodedHash.Split('.');
        if (parts.Length != 3)
            return false;

        if (!int.TryParse(parts[0], out int iterations) || iterations <= 0)
            return false;

        byte[] salt;
        byte[] expectedKey;
        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expectedKey = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        byte[] actualKey = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, expectedKey.Length);

        // CryptographicOperations.FixedTimeEquals: 상수 시간 비교로 타이밍 사이드채널 차단.
        return CryptographicOperations.FixedTimeEquals(actualKey, expectedKey);
    }
}
