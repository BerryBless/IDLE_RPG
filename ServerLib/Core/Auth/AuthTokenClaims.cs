namespace ServerLib.Core.Auth;

/// <summary>
/// <see cref="IAuthTokenValidator.TryValidate"/>가 유효한 토큰에서 복원한 클레임입니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe. <c>readonly struct</c>로 불변이며 스택/인라인 전달됩니다.
/// <b>[Memory Allocation:]</b> 구조체 자체는 Zero-allocation. <see cref="Username"/>은 검증 과정에서
/// 이미 힙에 할당된 string 참조를 담을 뿐 추가 할당을 일으키지 않습니다.
/// </remarks>
public readonly struct AuthTokenClaims : IEquatable<AuthTokenClaims>
{
    /// <summary>토큰이 가리키는 계정의 고유 식별자입니다.</summary>
    public int AccountId { get; }

    /// <summary>토큰 발급 시점의 사용자 이름입니다.</summary>
    public string Username { get; }

    /// <summary>토큰 만료 시각(UTC 기준)입니다.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>클레임 값을 지정하여 인스턴스를 생성합니다.</summary>
    /// <param name="accountId">계정 고유 식별자입니다.</param>
    /// <param name="username">사용자 이름입니다.</param>
    /// <param name="expiresAt">토큰 만료 시각입니다.</param>
    public AuthTokenClaims(int accountId, string username, DateTimeOffset expiresAt)
    {
        AccountId = accountId;
        Username = username;
        ExpiresAt = expiresAt;
    }

    /// <inheritdoc/>
    public bool Equals(AuthTokenClaims other) =>
        AccountId == other.AccountId
        && Username == other.Username
        && ExpiresAt == other.ExpiresAt;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is AuthTokenClaims other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(AccountId, Username, ExpiresAt);
}
