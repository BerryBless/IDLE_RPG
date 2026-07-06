using GameServer.Stats;

namespace GameServer.Items;

/// <summary>
/// 장비 마스터 데이터 테이블. 현재는 무기·방어구·장신구 각 5종(총 15종)을 C#으로 하드코딩해 보관한다.
/// </summary>
/// <remarks>
/// <b>[JSON 이관 대비]</b> 하드코딩 데이터는 <see cref="CreateDefault"/> 정적 팩토리 메서드
/// 안에서만 만들어진다(정적 생성자가 아니므로 실패 시 예외가 그대로 전파된다). 나중에 JSON
/// 파일 기반으로 옮길 때는 <c>EquipmentTable.FromJson(path)</c> 같은 별도 팩토리를 추가하기만
/// 하면 되고, <see cref="IMasterDataTable{TKey,T}"/>를 사용하는 다른 코드는 전혀 바뀔 필요가
/// 없다(코드리뷰 2026-07-06 H1 수정 — 이전에는 static class + 정적 필드 초기화식으로 고정되어
/// 있어 테스트에서 대체 데이터셋을 주입할 수 없었다).
/// <b>[ID 대역]</b> <see cref="EquipmentTemplate.ItemMetaId"/>는 무기 4000번대·방어구 5000번대·
/// 장신구 6000번대를 사용한다. 몬스터(<c>GameServer.Systems.MonsterTemplate.MonsterId</c>)의
/// 2000번대·드롭 아이템 3000번대와 겹치지 않게 채번한다(코드리뷰 2026-07-06 Low 수정 — 이전에는
/// 이 대역 규칙이 문서화되지 않고 암묵적으로만 지켜지고 있었다).
/// <b>[조회 로직]</b> 코드리뷰 2026-07-06 Medium 수정: 조회는 이제
/// <see cref="MasterDataTable{TKey,T}"/> 공통 기반의 Dictionary 인덱스를 사용한다(과거 foreach
/// 선형 탐색이 3개 테이블에 중복돼 있던 것을 제거).
/// </remarks>
public sealed class EquipmentTable : MasterDataTable<int, EquipmentTemplate>
{
    /// <summary>주어진 템플릿 목록으로 테이블을 구성한다(아이템 메타 ID 중복 시 즉시 실패).</summary>
    /// <param name="templates">등록할 장비 템플릿 목록</param>
    /// <exception cref="ArgumentException"><paramref name="templates"/>에 <see cref="EquipmentTemplate.ItemMetaId"/>
    /// 중복이 있는 경우</exception>
    public EquipmentTable(IReadOnlyList<EquipmentTemplate> templates)
        : base(templates, t => t.ItemMetaId, "장비 템플릿")
    {
    }

    /// <summary>하드코딩된 기본 15종(무기·방어구·장신구 각 5종) 데이터로 테이블을 생성한다.</summary>
    public static EquipmentTable CreateDefault() => new(BuildDefaultTemplates());

    private static List<EquipmentTemplate> BuildDefaultTemplates() => new()
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

}
