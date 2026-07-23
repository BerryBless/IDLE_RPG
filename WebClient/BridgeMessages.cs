using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebClient;

/// <summary>
/// 브라우저↔WebClient WebSocket에서 오가는 JSON 메시지 DTO 모음입니다. 바이너리 게임 프로토콜은
/// WebClient 내부에만 봉인하고 브라우저에는 사람이 읽을 수 있는 JSON만 노출합니다 — 게스트 토큰
/// 발급(비밀키)이 서버 측에만 있어야 하고, MVP instanceId→닉네임 역매핑 같은 의미 보강이 필요하며,
/// 트래픽이 초당 수 건 수준이라 JSON 오버헤드가 무의미하기 때문입니다(설계: plan/web_client_0723.md).
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 모든 DTO는 불변 record, <see cref="Json"/> 옵션 인스턴스는
/// 문서화된 Thread-safe 재사용 대상입니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 직렬화마다 JSON 문자열 할당 — 접속당 초당 ~5건
/// (MobHp 150ms 스로틀 + 저빈도 이벤트)이라 GC 영향은 무시 가능합니다.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 순수 CPU 직렬화.</description></item>
/// </list>
/// </remarks>
public static class BridgeMessages
{
    /// <summary>브라우저 JS가 <c>msg.type</c>처럼 camelCase로 읽으므로 MonitorServer와 동일하게
    /// camelCase 정책을 공유 인스턴스로 재사용합니다(Thread-safe).</summary>
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>브라우저→서버: 입장 요청. WebSocket을 연 뒤 보내는 첫(그리고 유일한) 메시지입니다.</summary>
    /// <param name="Type"><c>"join"</c></param>
    /// <param name="Nickname">희망 닉네임(비우면 서버가 자동 생성)</param>
    public sealed record JoinRequest(string? Type, string? Nickname);

    /// <summary>서버→브라우저: 게스트 신원 확정 통지(토큰은 노출하지 않음 — 브리지가 대신 제출).</summary>
    /// <param name="Type"><c>"joined"</c></param>
    /// <param name="Nickname">확정된 닉네임</param>
    /// <param name="AccountId">할당된 게스트 계정 ID</param>
    public sealed record JoinedMessage(string Type, string Nickname, int AccountId)
    {
        /// <summary>메시지를 생성합니다.</summary>
        public JoinedMessage(string nickname, int accountId) : this("joined", nickname, accountId) { }
    }

    /// <summary>서버→브라우저: GameServer 인증 결과(<c>AuthTokenAckPacket</c> 중계).</summary>
    /// <param name="Type"><c>"auth"</c></param>
    /// <param name="Success">인증 성공 여부</param>
    public sealed record AuthMessage(string Type, bool Success)
    {
        /// <summary>메시지를 생성합니다.</summary>
        public AuthMessage(bool success) : this("auth", success) { }
    }

    /// <summary>서버→브라우저: 공유 보스 HP 갱신(<c>MobHpPacket</c> 중계).</summary>
    /// <param name="Type"><c>"bossHp"</c></param>
    /// <param name="Hp">현재 HP(0..MaxHp — 서버가 클램프 보장)</param>
    /// <param name="MaxHp">최대 HP</param>
    /// <param name="Generation">보스 세대(리스폰마다 +1)</param>
    public sealed record BossHpMessage(string Type, long Hp, long MaxHp, int Generation)
    {
        /// <summary>메시지를 생성합니다.</summary>
        public BossHpMessage(long hp, long maxHp, int generation) : this("bossHp", hp, maxHp, generation) { }
    }

    /// <summary>서버→브라우저: 보스 처치 이벤트(<c>MobDeathPacket</c> 중계 + MVP 닉네임 역매핑).</summary>
    /// <param name="Type"><c>"bossDeath"</c></param>
    /// <param name="Generation">처치된 보스 세대</param>
    /// <param name="TopDamage">MVP 누적 데미지</param>
    /// <param name="MvpName">역매핑된 게스트 닉네임, 실패 시 서버 instanceId 원문</param>
    /// <param name="MvpIsMe">MVP가 이 접속의 게스트 본인인지</param>
    public sealed record BossDeathMessage(string Type, int Generation, long TopDamage, string MvpName, bool MvpIsMe)
    {
        /// <summary>메시지를 생성합니다.</summary>
        public BossDeathMessage(int generation, long topDamage, string mvpName, bool mvpIsMe)
            : this("bossDeath", generation, topDamage, mvpName, mvpIsMe) { }
    }

    /// <summary>서버→브라우저: 브리지 상태 전이 통지.</summary>
    /// <param name="Type"><c>"status"</c></param>
    /// <param name="State"><c>"connecting"</c>|<c>"fighting"</c>|<c>"disconnected"</c></param>
    public sealed record StatusMessage(string Type, string State)
    {
        /// <summary>메시지를 생성합니다.</summary>
        public StatusMessage(string state) : this("status", state) { }
    }

    /// <summary>서버→브라우저: 진행 불가 오류 통지(이후 브리지는 종료 수순).</summary>
    /// <param name="Type"><c>"error"</c></param>
    /// <param name="Message">사람이 읽을 수 있는 사유</param>
    public sealed record ErrorMessage(string Type, string Message)
    {
        /// <summary>메시지를 생성합니다.</summary>
        public ErrorMessage(string message) : this("error", message) { }
    }

    /// <summary>DTO를 브라우저로 보낼 JSON 문자열로 직렬화합니다(camelCase).</summary>
    /// <typeparam name="T">DTO 타입</typeparam>
    /// <param name="message">직렬화할 메시지</param>
    /// <returns>JSON 텍스트</returns>
    public static string Serialize<T>(T message) => JsonSerializer.Serialize(message, Json);

    /// <summary>브라우저가 보낸 JSON 텍스트를 <see cref="JoinRequest"/>로 파싱 시도합니다.</summary>
    /// <param name="text">수신 텍스트 프레임</param>
    /// <param name="request">성공 시 파싱 결과</param>
    /// <returns><c>type=="join"</c>인 유효 요청이면 <see langword="true"/></returns>
    public static bool TryParseJoin(string text, out JoinRequest request)
    {
        request = new JoinRequest(null, null);
        try
        {
            JoinRequest? parsed = JsonSerializer.Deserialize<JoinRequest>(text, Json);
            if (parsed is null || !string.Equals(parsed.Type, "join", StringComparison.Ordinal))
                return false;
            request = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false; // 손상된 JSON은 프로토콜 위반 — 호출부가 오류 응답 후 종료한다.
        }
    }
}
