using GameServer.Items;
using GameServer.Stats;

namespace GameServer.Entities;

/// <summary>사용자 계정이 소유·조작하는 플레이어 캐릭터.</summary>
public sealed class Player : Entity
{
    /// <summary>이 캐릭터를 소유한 계정의 고유 ID.</summary>
    public int AccountId { get; init; }

    /// <summary>현재 누적 경험치.</summary>
    public BigNumber CurrentExp { get; set; }

    /// <summary>현재 보유 골드.</summary>
    public BigNumber CurrentGold { get; set; }

    /// <summary>착용 장비를 관리하는 인벤토리.</summary>
    public EquipmentInventory Equipment { get; init; } = new();

    /// <summary>착용 장비가 제공하는 스탯 수정치를 반환한다.</summary>
    /// <returns><see cref="Equipment"/>가 제공하는 <see cref="StatModifier"/> 목록</returns>
    /// <remarks>
    /// 실제 집계(<c>Flat→PercentAdd→PercentMult</c> 순 적용, 버프 합산)는
    /// <see cref="Entity.UpdateFinalStats"/>의 공통 파이프라인이 수행한다. 이전에는 이 클래스가
    /// <c>UpdateFinalStats</c>를 직접 오버라이드해 <c>ModType</c>을 무시하고 전부 <c>+=</c>로 더했고,
    /// <see cref="Entity.BuffManager"/>도 반영하지 않는 버그가 있었다.
    /// </remarks>
    protected override List<StatModifier> GetExtraModifiers() => [.. Equipment.GetAllModifiers()];

    /// <summary>경험치를 획득하여 <see cref="CurrentExp"/>에 누적한다.</summary>
    /// <param name="amount">획득한 경험치량</param>
    public void AddExp(BigNumber amount) => CurrentExp += amount;

    /// <summary>골드를 획득하여 <see cref="CurrentGold"/>에 누적한다.</summary>
    /// <param name="amount">획득한 골드량</param>
    public void AddGold(BigNumber amount) => CurrentGold += amount;
}
