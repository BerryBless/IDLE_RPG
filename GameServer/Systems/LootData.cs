using GameServer.Items;
using GameServer.Stats;

namespace GameServer.Systems;

/// <summary>
/// 몬스터 처치 또는 오프라인 진행으로 발생한 보상 결과를 전달하는 불변 DTO.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 생성 후 값이 변경되지 않는 전달 전용 데이터 객체로 사용하는 것을 전제로 한다 (Thread-safe).</description></item>
/// </list>
/// </remarks>
public sealed class LootData
{
    /// <summary>획득한 총 경험치.</summary>
    public BigNumber TotalExp { get; init; }

    /// <summary>획득한 총 골드.</summary>
    public BigNumber TotalGold { get; init; }

    /// <summary>획득한 아이템 목록.</summary>
    public List<Item> AcquiredItems { get; init; } = new();
}
