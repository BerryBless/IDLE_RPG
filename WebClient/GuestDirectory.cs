using System.Collections.Concurrent;

namespace WebClient;

/// <summary>
/// 이 프로세스가 발급한 게스트 계정 ID → 닉네임 매핑 디렉터리입니다. 레이드 MVP 브로드캐스트
/// (<see cref="ServerLib.Core.Serialization.Packets.MobDeathPacket.MvpName"/>)가 닉네임이 아닌
/// 서버 내부 instanceId(<c>player-{accountId}-{sessionId:N}</c>, GameServer <c>SessionAuthGate</c> 참조)로
/// 오기 때문에, 브라우저에 사람이 읽을 닉네임을 보여주려면 accountId를 파싱해 역매핑해야 합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 모든 상태가 <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// 하나이므로 여러 브리지(요청 스레드)와 I/O 콜백 스레드가 동시에 등록/해제/조회해도 안전합니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 등록 시 딕셔너리 엔트리 1개. <see cref="TryResolveMvp"/>는
/// 성공 시 닉네임 참조만 반환(무복사), 파싱은 Span 기반 무할당.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 모든 연산이 즉시 반환합니다.</description></item>
/// </list>
/// </remarks>
public sealed class GuestDirectory
{
    /// <summary>MVP instanceId 접두사. GameServer/Systems/SessionAuthGate.cs의
    /// <c>$"player-{claims.AccountId}-{session.SessionId:N}"</c> 포맷에 의존합니다 —
    /// 그쪽 포맷이 바뀌면 여기 파싱도 함께 갱신해야 합니다(테스트로 고정).</summary>
    private const string InstanceIdPrefix = "player-";

    // ConcurrentDictionary: 브리지 시작(등록)·종료(해제)는 요청 스레드에서, MVP 역매핑(조회)은
    // ServerLib I/O 콜백 스레드에서 일어난다 — 락 없는 세그먼트 분할 해시로 두 경로가 경합 없이 공존.
    private readonly ConcurrentDictionary<int, string> _nicknamesByAccountId = new();

    /// <summary>게스트 계정을 디렉터리에 등록합니다. 같은 ID 재등록 시 닉네임을 덮어씁니다.</summary>
    /// <param name="accountId">이 프로세스가 발급한 게스트 계정 ID</param>
    /// <param name="nickname">브라우저에 표시할 닉네임</param>
    public void Register(int accountId, string nickname) => _nicknamesByAccountId[accountId] = nickname;

    /// <summary>브리지 종료 시 게스트 계정을 디렉터리에서 제거합니다. 미등록 ID면 무시합니다.</summary>
    /// <param name="accountId">제거할 게스트 계정 ID</param>
    public void Unregister(int accountId) => _nicknamesByAccountId.TryRemove(accountId, out _);

    /// <summary>
    /// MVP instanceId(<c>player-{accountId}-{sessionId}</c>)에서 accountId를 파싱해 이 프로세스가
    /// 발급한 게스트 닉네임으로 역매핑을 시도합니다.
    /// </summary>
    /// <param name="mvpInstanceId">MobDeathPacket.MvpName으로 수신한 서버 내부 instanceId</param>
    /// <param name="accountId">파싱에 성공하면 instanceId에 박힌 계정 ID(디렉터리 미등록이어도 채워짐)</param>
    /// <param name="nickname">역매핑에 성공하면 게스트 닉네임</param>
    /// <returns>포맷 파싱과 디렉터리 조회가 모두 성공하면 <see langword="true"/>.
    /// 포맷이 다르거나(다른 프로세스의 플레이어 등) 이 프로세스 발급 계정이 아니면 <see langword="false"/></returns>
    public bool TryResolveMvp(string mvpInstanceId, out int accountId, out string nickname)
    {
        accountId = 0;
        nickname = string.Empty;

        if (!mvpInstanceId.StartsWith(InstanceIdPrefix, StringComparison.Ordinal))
            return false;

        // "player-" 이후 다음 '-'까지가 accountId 10진수. Span 슬라이스로 무할당 파싱.
        ReadOnlySpan<char> rest = mvpInstanceId.AsSpan(InstanceIdPrefix.Length);
        int dash = rest.IndexOf('-');
        if (dash <= 0)
            return false;
        if (!int.TryParse(rest[..dash], out accountId))
            return false;

        if (!_nicknamesByAccountId.TryGetValue(accountId, out string? found))
            return false;

        nickname = found;
        return true;
    }
}
