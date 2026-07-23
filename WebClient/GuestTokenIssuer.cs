using ServerLib.Core.Auth;

namespace WebClient;

/// <summary>발급된 게스트 신원 1건입니다. GameServer 인증 게이트에 그대로 제출할 토큰을 포함합니다.</summary>
/// <param name="AccountId">이 프로세스가 할당한 게스트 계정 ID(<see cref="GuestTokenIssuer.FirstGuestAccountId"/> 대역)</param>
/// <param name="Nickname">정제된(또는 자동 생성된) 닉네임</param>
/// <param name="Token">GameServer <c>SessionAuthGate</c>가 검증할 HMAC 토큰 문자열</param>
public readonly record struct GuestIdentity(int AccountId, string Nickname, string Token);

/// <summary>
/// 게스트 로그인용 HMAC 토큰 발급기입니다. AuthServer/MongoDB를 거치지 않고
/// <see cref="HmacAuthTokenCodec"/>으로 직접 토큰을 발급합니다(LoadTester <c>--mode game</c>의
/// <c>LocalHmacTokenSource</c>와 동일 원리). GameServer와 동일한 비밀키
/// (<see cref="HmacSecretResolver"/>)를 공유해야 검증을 통과합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 계정 ID 할당은 <see cref="Interlocked"/> 단조 증가,
/// <see cref="HmacAuthTokenCodec"/>은 불변, 디렉터리 등록은 <see cref="GuestDirectory"/>가 보장합니다.
/// 여러 브라우저가 동시에 입장해도 계정 ID가 중복되지 않습니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 발급마다 토큰·닉네임 문자열 등 소량 할당(입장 1회 저빈도 경로).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 네트워크 왕복 없이 순수 HMAC 연산으로 동기 완료합니다.</description></item>
/// </list>
/// </remarks>
public sealed class GuestTokenIssuer
{
    /// <summary>게스트 계정 ID 시작 대역입니다. AuthServer 시딩 더미 계정(0..2999)과 절대 겹치지 않도록
    /// 충분히 떨어진 양수 대역을 사용합니다(로그·MVP instanceId 가독성 위해 음수 대신 양수 선택).</summary>
    public const int FirstGuestAccountId = 1_000_000;

    /// <summary>닉네임 최대 길이(초과분은 절단)입니다.</summary>
    public const int MaxNicknameLength = 16;

    private readonly HmacAuthTokenCodec _codec;
    private readonly GuestDirectory _directory;
    private readonly TimeSpan _tokenTtl;

    // Interlocked.Increment 대상: 프로세스 수명 동안 단조 증가하는 게스트 계정 ID 카운터.
    // CAS 기반 원자 증가라 락 없이 다중 요청 스레드에서 유일성이 보장된다.
    private int _lastAccountId = FirstGuestAccountId - 1;

    /// <summary>발급기를 생성합니다.</summary>
    /// <param name="codec">GameServer와 동일 비밀키로 생성된 토큰 코덱</param>
    /// <param name="directory">발급 즉시 accountId→닉네임을 등록할 디렉터리(MVP 역매핑용)</param>
    /// <param name="tokenTtl">발급 토큰 유효기간. 만료 후에도 이미 인증된 세션은 유지된다
    /// (GameServer는 접속 시 1회만 검증) — 재접속 시에는 새 토큰을 발급하므로 짧아도 무방</param>
    public GuestTokenIssuer(HmacAuthTokenCodec codec, GuestDirectory directory, TimeSpan tokenTtl)
    {
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(directory);

        _codec = codec;
        _directory = directory;
        _tokenTtl = tokenTtl;
    }

    /// <summary>
    /// 요청 닉네임을 정제해 게스트 신원을 발급하고 디렉터리에 등록합니다.
    /// 닉네임이 비었으면 <c>Guest-XXXX</c>(계정 ID 하위 4자리)를 자동 생성합니다.
    /// </summary>
    /// <param name="requestedNickname">브라우저가 보낸 원시 닉네임(null/공백 허용)</param>
    /// <returns>계정 ID·정제 닉네임·HMAC 토큰이 담긴 게스트 신원</returns>
    public GuestIdentity Issue(string? requestedNickname)
    {
        int accountId = Interlocked.Increment(ref _lastAccountId);
        string nickname = SanitizeNickname(requestedNickname, accountId);
        string token = _codec.Issue(accountId, nickname, DateTimeOffset.UtcNow + _tokenTtl);
        _directory.Register(accountId, nickname);
        return new GuestIdentity(accountId, nickname, token);
    }

    /// <summary>닉네임을 정제합니다: 제어 문자 제거 → 양끝 공백 제거 → 길이 제한 → 비면 자동 생성.</summary>
    /// <param name="raw">원시 닉네임</param>
    /// <param name="accountId">자동 생성 시 하위 4자리를 쓸 계정 ID</param>
    /// <returns>표시 가능한 닉네임(항상 1자 이상)</returns>
    internal static string SanitizeNickname(string? raw, int accountId)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            // 제어 문자(개행·NUL 등)는 콘솔 로그·HTML 표시 양쪽에서 문제를 일으키므로 제거한다.
            Span<char> buffer = stackalloc char[Math.Min(raw.Length, MaxNicknameLength * 2)];
            int written = 0;
            foreach (char c in raw)
            {
                if (char.IsControl(c))
                    continue;
                buffer[written++] = c;
                if (written == buffer.Length)
                    break;
            }

            string cleaned = new string(buffer[..written]).Trim();
            if (cleaned.Length > MaxNicknameLength)
                cleaned = cleaned[..MaxNicknameLength];
            if (cleaned.Length > 0)
                return cleaned;
        }

        return $"Guest-{accountId % 10000:D4}";
    }
}
