using GameServer.Stats;

namespace GameServer.Systems;

/// <summary>
/// <see cref="Entities.Monster"/>가 소유하는 보상 정의. 처치 시 지급할 경험치·골드·아이템 드롭 테이블을 담는다.
/// </summary>
public sealed class RewardComponent
{
    /// <summary>1회 처치당 지급되는 경험치.</summary>
    public BigNumber ExpDrop { get; init; }

    /// <summary>1회 처치당 지급되는 골드.</summary>
    public BigNumber GoldDrop { get; init; }

    /// <summary>아이템 드롭 확률표.</summary>
    public List<DropPool> DropTable { get; init; } = new();

    /// <summary>
    /// 지정한 처치 횟수만큼의 보상(경험치·골드·아이템)을 확률적으로 산출한다.
    /// </summary>
    /// <param name="killCount">처치 횟수 (오프라인 방치 시간 동안의 누적 처치 수 등)</param>
    /// <returns>산출된 보상 데이터</returns>
    public LootData GenerateLoot(int killCount) => throw new NotImplementedException();
}
