namespace AuthServer.Login;

/// <summary>
/// <see cref="LoginService.AuthenticateAsync"/>의 결과입니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe. <c>readonly struct</c>로 불변입니다.
/// <b>[Memory Allocation:]</b> 구조체 자체는 Zero-allocation. <see cref="Token"/>은 이미 발급된
/// 문자열 참조를 담을 뿐입니다.
/// </remarks>
public readonly struct LoginResult
{
    /// <summary>로그인 성공 여부입니다.</summary>
    public bool Success { get; }

    /// <summary>성공 시 발급된 토큰입니다. 실패 시 빈 문자열입니다.</summary>
    public string Token { get; }

    private LoginResult(bool success, string token)
    {
        Success = success;
        Token = token;
    }

    /// <summary>성공 결과를 생성합니다.</summary>
    /// <param name="token">발급된 토큰입니다.</param>
    public static LoginResult Succeeded(string token) => new(true, token);

    /// <summary>실패 결과를 생성합니다(토큰은 항상 빈 문자열).</summary>
    public static LoginResult Failed() => new(false, string.Empty);
}
