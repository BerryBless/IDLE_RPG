using GameServer.Items;
using GameServer.Stats;

namespace GameServer.Tests.Items;

/// <summary>
/// 코드리뷰 F8 회귀 테스트: GetAllModifiers가 더 이상 동일 StatType+ModType 모디파이어를
/// 미리 합산(Sum)하지 않고 개별 항목 그대로 반환하는지 검증한다. Entity.UpdateFinalStats가
/// 이미 올바르게(Flat/PercentAdd 합산, PercentMult 곱연산) 집계하므로, 장비 계층의 사전 병합은
/// PercentMult에서만 "장비 내부는 가산, 소스 간은 곱연산"이라는 불일치를 만들던 원인이었다.
/// </summary>
public class EquipmentInventoryTests
{
    [Fact]
    public void GetAllModifiers_TwoPercentMultOnSameWeapon_ReturnsBothEntriesUnmerged()
    {
        var inventory = new EquipmentInventory();
        var weapon = new Weapon
        {
            InstanceId = "w1",
            ItemMetaId = 1,
            Name = "테스트 검",
            BaseModifiers =
            [
                new StatModifier { StatType = StatType.Atk, ModType = ModifierType.PercentMult, Value = 0.1 },
                new StatModifier { StatType = StatType.Atk, ModType = ModifierType.PercentMult, Value = 0.2 }
            ]
        };
        inventory.Equip(weapon, SlotType.Weapon);

        var modifiers = inventory.GetAllModifiers();

        Assert.Equal(2, modifiers.Count);
    }

    [Fact]
    public void GetAllModifiers_FlatAddAcrossItems_StillReturnsAllContributions()
    {
        var inventory = new EquipmentInventory();
        var weapon = new Weapon
        {
            InstanceId = "w1",
            ItemMetaId = 1,
            Name = "테스트 검",
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 10 }]
        };
        var armor = new Armor
        {
            InstanceId = "a1",
            ItemMetaId = 2,
            Name = "테스트 방어구",
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 5 }]
        };
        inventory.Equip(weapon, SlotType.Weapon);
        inventory.Equip(armor, SlotType.Armor);

        var modifiers = inventory.GetAllModifiers();

        Assert.Equal(2, modifiers.Count);
        Assert.Equal(15, modifiers.Sum(m => m.Value));
    }

    [Fact]
    public void GetAllModifiers_CachesUntilEquipmentChanges()
    {
        var inventory = new EquipmentInventory();
        var weapon = new Weapon
        {
            InstanceId = "w1",
            ItemMetaId = 1,
            Name = "테스트 검",
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 10 }]
        };
        inventory.Equip(weapon, SlotType.Weapon);

        var first = inventory.GetAllModifiers();
        var second = inventory.GetAllModifiers();

        Assert.Same(first, second); // 장비 변경 없으면 동일 캐시 인스턴스 반환
    }
}
