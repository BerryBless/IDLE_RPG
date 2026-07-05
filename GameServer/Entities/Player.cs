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

    /// <summary>장착 무기의 공격 배율을 반환한다. 무기 미착용 시 1.0(배율 없음).</summary>
    /// <returns>장착 무기의 <see cref="Items.Weapon.AttackScaling"/>, 없으면 1.0</returns>
    /// <remarks>
    /// 코드리뷰 F1 수정 전에는 호출부(Main.cs 등)가 <c>?? 0</c>으로 무기 미착용 시 배율을 0으로
    /// 취급해 맨손 공격이 데미지 0이 되는 부수적 결함이 있었다. 기본값 1.0으로 해소한다.
    /// </remarks>
    protected override double GetAttackScaling() => Equipment.GetWeapon()?.AttackScaling ?? 1.0;

    /// <summary>경험치를 획득하여 <see cref="CurrentExp"/>에 누적한다.</summary>
    /// <param name="amount">획득한 경험치량</param>
    public void AddExp(BigNumber amount) => CurrentExp += amount;

    /// <summary>골드를 획득하여 <see cref="CurrentGold"/>에 누적한다.</summary>
    /// <param name="amount">획득한 골드량</param>
    public void AddGold(BigNumber amount) => CurrentGold += amount;
}
