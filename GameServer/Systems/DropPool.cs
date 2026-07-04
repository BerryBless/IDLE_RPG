namespace GameServer.Systems;

/// <summary>
/// <see cref="RewardComponent.DropTable"/>의 한 항목. 특정 아이템이 드롭될 확률과 개수 범위를 정의한다.
/// 다이어그램에는 참조만 되고 정의되지 않아 신규로 정의했다.
/// </summary>
public sealed class DropPool
{
    /// <summary>드롭될 아이템의 정의 테이블(마스터 데이터) ID.</summary>
    public int ItemMetaId { get; init; }

    /// <summary>드롭 확률 (0.0 ~ 1.0).</summary>
    public float DropChance { get; init; }

    /// <summary>드롭 시 최소 개수.</summary>
    public int MinQty { get; init; }

    /// <summary>드롭 시 최대 개수.</summary>
    public int MaxQty { get; init; }
}
