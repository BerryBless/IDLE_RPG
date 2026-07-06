using GameServer.Stats;

namespace GameServer.Items;

/// <summary>
/// 장비 마스터 데이터 테이블. 현재는 무기·방어구·장신구 각 5종(총 15종)을 C#으로 하드코딩해 보관한다.
/// </summary>
/// <remarks>
/// <b>[JSON 이관 대비]</b> <see cref="All"/>의 초기화 리스트는 이 파일에만 격리되어 있다. 나중에
/// JSON 파일 기반으로 옮길 때는 이 프로퍼티의 초기화식만
/// <c>JsonSerializer.Deserialize&lt;List&lt;EquipmentTemplate&gt;&gt;(File.ReadAllText(path))</c>로
/// 교체하면 되고, <see cref="EquipmentTemplate"/>·<see cref="GetById"/>를 사용하는 다른 코드는
/// 전혀 바뀔 필요가 없다.
/// </remarks>
public static class EquipmentTable
{
    /// <summary>등록된 전체 장비 종 목록(슬롯별 티어 오름차순).</summary>
    public static IReadOnlyList<EquipmentTemplate> All { get; } = new List<EquipmentTemplate>
    {
        // ===== 무기 (4001~4005) =====
        new()
        {
            ItemMetaId = 4001, Name = "낡은 검", Slot = SlotType.Weapon, AttackScaling = 1.5f,
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 1 }]
        },
        new()
        {
            ItemMetaId = 4002, Name = "청동 검", Slot = SlotType.Weapon, AttackScaling = 1.6f,
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 5 }]
        },
        new()
        {
            ItemMetaId = 4003, Name = "강철 검", Slot = SlotType.Weapon, AttackScaling = 1.8f,
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 12 }]
        },
        new()
        {
            ItemMetaId = 4004, Name = "미스릴 검", Slot = SlotType.Weapon, AttackScaling = 2.0f,
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 20 }]
        },
        new()
        {
            ItemMetaId = 4005, Name = "용살검", Slot = SlotType.Weapon, AttackScaling = 2.5f,
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 35 }]
        },

        // ===== 방어구 (5001~5005) =====
        new()
        {
            ItemMetaId = 5001, Name = "가죽 갑옷", Slot = SlotType.Armor,
            BaseModifiers = [new StatModifier { StatType = StatType.Def, ModType = ModifierType.FlatAdd, Value = 3 }]
        },
        new()
        {
            ItemMetaId = 5002, Name = "사슬 갑옷", Slot = SlotType.Armor,
            BaseModifiers = [new StatModifier { StatType = StatType.Def, ModType = ModifierType.FlatAdd, Value = 8 }]
        },
        new()
        {
            ItemMetaId = 5003, Name = "판금 갑옷", Slot = SlotType.Armor,
            BaseModifiers =
            [
                new StatModifier { StatType = StatType.Def, ModType = ModifierType.FlatAdd, Value = 15 },
                new StatModifier { StatType = StatType.Hp, ModType = ModifierType.FlatAdd, Value = 5 }
            ]
        },
        new()
        {
            ItemMetaId = 5004, Name = "미스릴 갑옷", Slot = SlotType.Armor,
            BaseModifiers =
            [
                new StatModifier { StatType = StatType.Def, ModType = ModifierType.FlatAdd, Value = 25 },
                new StatModifier { StatType = StatType.Recovery, ModType = ModifierType.FlatAdd, Value = 10 }
            ]
        },
        new()
        {
            ItemMetaId = 5005, Name = "용비늘 갑옷", Slot = SlotType.Armor,
            BaseModifiers =
            [
                new StatModifier { StatType = StatType.Def, ModType = ModifierType.FlatAdd, Value = 40 },
                new StatModifier { StatType = StatType.Hp, ModType = ModifierType.FlatAdd, Value = 20 }
            ]
        },

        // ===== 장신구 (6001~6005) =====
        new()
        {
            ItemMetaId = 6001, Name = "낡은 반지", Slot = SlotType.Accessory,
            BaseModifiers = [new StatModifier { StatType = StatType.CritProb, ModType = ModifierType.FlatAdd, Value = 0.02 }]
        },
        new()
        {
            ItemMetaId = 6002, Name = "은목걸이", Slot = SlotType.Accessory,
            BaseModifiers = [new StatModifier { StatType = StatType.CritProb, ModType = ModifierType.FlatAdd, Value = 0.05 }]
        },
        new()
        {
            ItemMetaId = 6003, Name = "마력의 귀걸이", Slot = SlotType.Accessory,
            BaseModifiers = [new StatModifier { StatType = StatType.CritDmg, ModType = ModifierType.FlatAdd, Value = 0.1 }]
        },
        new()
        {
            ItemMetaId = 6004, Name = "흡혈의 반지", Slot = SlotType.Accessory,
            BaseModifiers = [new StatModifier { StatType = StatType.Lifesteal, ModType = ModifierType.FlatAdd, Value = 0.05 }]
        },
        new()
        {
            ItemMetaId = 6005, Name = "대현자의 목걸이", Slot = SlotType.Accessory,
            BaseModifiers =
            [
                new StatModifier { StatType = StatType.ManaRegen, ModType = ModifierType.FlatAdd, Value = 5 },
                new StatModifier { StatType = StatType.Mana, ModType = ModifierType.FlatAdd, Value = 20 }
            ]
        }
    };

    /// <summary>지정한 ID의 장비 템플릿을 찾는다.</summary>
    /// <param name="itemMetaId">조회할 아이템 메타 ID</param>
    /// <returns>일치하는 <see cref="EquipmentTemplate"/></returns>
    /// <exception cref="KeyNotFoundException">일치하는 장비가 없는 경우 — 마스터 데이터 설정
    /// 오류를 조용히 넘기지 않고 즉시 실패시킨다.</exception>
    public static EquipmentTemplate GetById(int itemMetaId)
    {
        foreach (var template in All)
        {
            if (template.ItemMetaId == itemMetaId)
            {
                return template;
            }
        }

        throw new KeyNotFoundException($"ItemMetaId {itemMetaId}에 해당하는 장비 템플릿을 찾을 수 없습니다.");
    }
}
