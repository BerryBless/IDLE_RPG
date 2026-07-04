using GameServer.Entities;

namespace GameServer.Systems;

/// <summary>
/// 플레이어가 접속하지 않은 동안의 방치 진행(오프라인 사냥)을 계산하는 전역 시스템.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 싱글턴으로 사용할 경우 호출 측에서 동시 호출을 직렬화해야 한다.</description></item>
/// <item><description><b>Blocking 여부:</b> 순수 계산 로직으로 설계되어야 하며, 내부에서 DB/File I/O 등 동기 블로킹을 수행해서는 안 된다.</description></item>
/// </list>
/// </remarks>
public sealed class OfflineProgressionManager
{
    /// <summary>
    /// 오프라인 경과 시간 동안 지정한 스테이지 몬스터를 상대로 발생했을 전투 결과를 계산하여 보상을 반환한다.
    /// </summary>
    /// <param name="player">보상을 지급받을 플레이어</param>
    /// <param name="stageMonster">플레이어가 방치 중이던 스테이지의 몬스터</param>
    /// <param name="offlineSeconds">오프라인 상태로 경과한 시간(초)</param>
    /// <returns>산출된 오프라인 보상 데이터</returns>
    public LootData ProcessOfflineTime(Player player, Monster stageMonster, int offlineSeconds) => throw new NotImplementedException();
}
