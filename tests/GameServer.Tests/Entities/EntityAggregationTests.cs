using GameServer.Combat;
using GameServer.Entities;
using GameServer.Items;
using GameServer.Stats;

namespace GameServer.Tests.Entities;

/// <summary>
/// Entity.UpdateFinalStats()의 통합 스탯 집계 파이프라인(Base + GetExtraModifiers + Buff, Flat→PercentAdd→PercentMult 순)을 검증한다.
/// </summary>
public class EntityAggregationTests
{
    private static Player MakePlayer(BaseStats? baseStats = null, Traits? baseTraits = null) => new()
    {
        InstanceId = "player-test",
        AccountId = 1,
        Level = 1,
        BaseStats = baseStats ?? new BaseStats { Atk = 100, Def = 20, Hp = 500, Recovery = 5, Mana = 50, ManaRegen = 2 },
        BaseTraits = baseTraits ?? new Traits()
    };

    [Fact]
    public void NoModifiers_FinalStatsEqualBaseStats()
    {
        var player = MakePlayer();

        player.UpdateFinalStats();

        Assert.Equal(100, player.FinalStats.Atk);
        Assert.Equal(20, player.FinalStats.Def);
        Assert.Equal(500, player.FinalStats.MaxHp);
        Assert.Equal(5, player.FinalStats.Recovery);
        Assert.Equal(50, player.FinalStats.MaxMana);
        Assert.Equal(2, player.FinalStats.ManaRegen);
    }

    [Fact]
    public void EquipmentFlatAdd_AddsToBaseValue()
    {
        var player = MakePlayer();
        var weapon = new Weapon
        {
            InstanceId = "w1",
            ItemMetaId = 1,
            Name = "테스트 검",
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 50 }]
        };
        player.Equipment.Equip(weapon, SlotType.Weapon);

        player.UpdateFinalStats();

        Assert.Equal(150, player.FinalStats.Atk);
    }

    [Fact]
    public void PercentAdd_AppliesAfterFlatAdd()
    {
        var player = MakePlayer();
        var weapon = new Weapon
        {
            InstanceId = "w1",
            ItemMetaId = 1,
            Name = "테스트 검",
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 50 }]
        };
        var armor = new Armor
        {
            InstanceId = "a1",
            ItemMetaId = 2,
            Name = "테스트 방어구",
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.PercentAdd, Value = 0.2 }]
        };
        player.Equipment.Equip(weapon, SlotType.Weapon);
        player.Equipment.Equip(armor, SlotType.Armor);

        player.UpdateFinalStats();

        // (100 + 50) * 1.2 = 180
        Assert.Equal(180, player.FinalStats.Atk, precision: 5);
    }

    [Fact]
    public void PercentMult_AppliesAfterPercentAdd()
    {
        var player = MakePlayer();
        var armor = new Armor
        {
            InstanceId = "a1",
            ItemMetaId = 2,
            Name = "테스트 방어구",
            BaseModifiers =
            [
                new StatModifier { StatType = StatType.Atk, ModType = ModifierType.PercentAdd, Value = 0.5 },
                new StatModifier { StatType = StatType.Atk, ModType = ModifierType.PercentMult, Value = 0.5 }
            ]
        };
        player.Equipment.Equip(armor, SlotType.Armor);

        player.UpdateFinalStats();

        // (100 + 0) * (1 + 0.5) * (1 + 0.5) = 225
        Assert.Equal(225, player.FinalStats.Atk, precision: 5);
    }

    [Fact]
    public void MultiplePercentMult_FromDifferentSources_MultiplyIndependently()
    {
        // 참고: EquipmentInventory.GetAllModifiers()는 동일 StatType+ModType 수정치를 소스(장비) 내부에서
        // 먼저 합산하므로(기존 구현, 이번 스코프 밖), "독립 곱연산"은 서로 다른 소스(장비 vs 버프)일 때 관찰된다.
        var player = MakePlayer();
        var armor = new Armor
        {
            InstanceId = "a1",
            ItemMetaId = 2,
            Name = "테스트 방어구",
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.PercentMult, Value = 0.1 }]
        };
        player.Equipment.Equip(armor, SlotType.Armor);
        player.BuffManager.ApplyEffect(new StatusEffect
        {
            EffectId = "buff-mult",
            MaxDuration = 10f,
            TimeRemaining = 10f,
            Modifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.PercentMult, Value = 0.2 }]
        });

        player.UpdateFinalStats();

        // 100 * 1.1 * 1.2 = 132
        Assert.Equal(132, player.FinalStats.Atk, precision: 5);
    }

    [Fact]
    public void BuffModifiers_AreIncludedInAggregation()
    {
        var player = MakePlayer();
        player.BuffManager.ApplyEffect(new StatusEffect
        {
            EffectId = "buff-atk",
            MaxDuration = 10f,
            TimeRemaining = 10f,
            Modifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 25 }]
        });

        player.UpdateFinalStats();

        Assert.Equal(125, player.FinalStats.Atk);
    }

    [Fact]
    public void EquipmentAndBuff_FlatAddModifiersAreSummedTogether()
    {
        var player = MakePlayer();
        var weapon = new Weapon
        {
            InstanceId = "w1",
            ItemMetaId = 1,
            Name = "테스트 검",
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 50 }]
        };
        player.Equipment.Equip(weapon, SlotType.Weapon);
        player.BuffManager.ApplyEffect(new StatusEffect
        {
            EffectId = "buff-atk",
            MaxDuration = 10f,
            TimeRemaining = 10f,
            Modifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 25 }]
        });

        player.UpdateFinalStats();

        Assert.Equal(175, player.FinalStats.Atk);
    }

    [Fact]
    public void TraitStats_AreAggregatedIntoCombatTraits()
    {
        var player = MakePlayer(baseTraits: new Traits { AtkSpeed = 1.0, CritProb = 0.1 });
        player.BuffManager.ApplyEffect(new StatusEffect
        {
            EffectId = "buff-haste",
            MaxDuration = 10f,
            TimeRemaining = 10f,
            Modifiers = [new StatModifier { StatType = StatType.AtkSpeed, ModType = ModifierType.FlatAdd, Value = 0.5 }]
        });

        player.UpdateFinalStats();

        Assert.Equal(1.5, player.FinalStats.CombatTraits.AtkSpeed, precision: 5);
        Assert.Equal(0.1, player.FinalStats.CombatTraits.CritProb, precision: 5);
    }

    [Fact]
    public void CallingUpdateFinalStats_Twice_DoesNotAccumulateModifiers()
    {
        var player = MakePlayer(baseTraits: new Traits { AtkSpeed = 1.0 });
        var weapon = new Weapon
        {
            InstanceId = "w1",
            ItemMetaId = 1,
            Name = "테스트 검",
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 50 }]
        };
        player.Equipment.Equip(weapon, SlotType.Weapon);

        player.UpdateFinalStats();
        player.UpdateFinalStats();
        player.UpdateFinalStats();

        Assert.Equal(150, player.FinalStats.Atk);
        Assert.Equal(1.0, player.FinalStats.CombatTraits.AtkSpeed, precision: 5);
    }

    [Fact]
    public void Monster_UsesMonsterAffixesAsExtraModifiers()
    {
        var monster = new Monster
        {
            InstanceId = "monster-test",
            MonsterId = 1,
            Level = 1,
            BaseStats = new BaseStats { Atk = 80 },
            MonsterAffixes = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 20 }]
        };

        monster.UpdateFinalStats();

        Assert.Equal(100, monster.FinalStats.Atk);
    }
}
