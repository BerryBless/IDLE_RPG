using GameServer.Entities;

namespace GameServer.Systems;

/// <summary>
/// <see cref="BattleTickEvent"/>를 사람이 읽을 콘솔 로그 문자열로 변환한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 정적 필드나 공유 가변 상태가 없고,
/// 인자로 받은 값만으로 새 문자열을 계산해 반환하는 순수 함수다. 여러 샤드 스레드가 동시에
/// 호출해도 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 호출마다 보간 문자열 1개를 새로 할당한다
/// (<see cref="BattleTickEvent.None"/>이면 <see cref="string.Empty"/>를 반환해 할당 없음).</description></item>
/// <item><description><b>Blocking 여부:</b> 즉시 반환(동기, non-blocking). I/O 없음(콘솔 출력은
/// 호출 측 책임).</description></item>
/// </list>
/// </remarks>
public static class BattleEventLogger
{
    /// <summary>
    /// 이번 틱의 사망 이벤트를 <paramref name="instanceId"/>가 식별하는 플레이어 기준으로 포맷한다.
    /// </summary>
    /// <param name="instanceId">이벤트가 발생한 플레이어의 <see cref="Entity.InstanceId"/></param>
    /// <param name="result">포맷할 사망 이벤트</param>
    /// <param name="player">누적 경험치·골드·레벨을 읽어올 플레이어(주로 <paramref name="instanceId"/> 소유자)</param>
    /// <returns>
    /// <see cref="BattleTickEvent.MonsterDefeated"/>/<see cref="BattleTickEvent.PlayerDefeated"/>는
    /// 프리픽스가 붙은 한 줄 로그 문자열, <see cref="BattleTickEvent.None"/>은 <see cref="string.Empty"/>.
    /// </returns>
    /// <remarks>
    /// 다중 플레이어 샤드 루프에서 매 틱 HP 상태까지 출력하면 콘솔이 넘치므로(스레드당 100명 규모),
    /// 호출 측(<c>Main.cs</c>)이 <see cref="BattleTickEvent.None"/>일 때 이 결과를 그대로 출력하지
    /// 않도록 빈 문자열로 신호를 준다.
    /// </remarks>
    public static string Format(string instanceId, BattleTickEvent result, Player player) => result switch
    {
        BattleTickEvent.MonsterDefeated =>
            $"[{instanceId}] [처치] 몬스터 처치! Lv.{player.Level} 누적 Exp={player.CurrentExp}, Gold={player.CurrentGold}",
        BattleTickEvent.PlayerDefeated => $"[{instanceId}] [부활] 플레이어 사망 → 즉시 부활",
        _ => string.Empty
    };
}
