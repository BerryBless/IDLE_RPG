using GameServer.Combat;
using GameServer.Stats;

namespace GameServer.Tests.Combat;

public class BuffManagerTests
{
    private static StatusEffect MakeEffect(string id, float timeRemaining, params StatModifier[] modifiers) => new()
    {
        EffectId = id,
        MaxDuration = timeRemaining,
        TimeRemaining = timeRemaining,
        Modifiers = [.. modifiers]
    };

    [Fact]
    public void ApplyEffect_AddsEffectToActiveModifiers()
    {
        var manager = new BuffManager();
        var modifier = new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 10 };
        var effect = MakeEffect("buff-atk", 5f, modifier);

        manager.ApplyEffect(effect);

        Assert.Single(manager.GetAllActiveModifiers());
    }

    [Fact]
    public void RemoveEffect_RemovesEffectImmediately()
    {
        var manager = new BuffManager();
        var effect = MakeEffect("buff-atk", 5f, new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 10 });
        manager.ApplyEffect(effect);

        manager.RemoveEffect(effect);

        Assert.Empty(manager.GetAllActiveModifiers());
    }

    [Fact]
    public void Update_TicksActiveEffects_AndRemovesExpiredOnes()
    {
        var manager = new BuffManager();
        var shortLived = MakeEffect("buff-short", 1f, new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 5 });
        var longLived = MakeEffect("buff-long", 10f, new StatModifier { StatType = StatType.Def, ModType = ModifierType.FlatAdd, Value = 3 });
        manager.ApplyEffect(shortLived);
        manager.ApplyEffect(longLived);

        manager.Update(1.5f); // shortLived(1f) 만료, longLived(10f)는 8.5f 남음

        var remaining = manager.GetAllActiveModifiers();
        Assert.Single(remaining);
        Assert.Equal(StatType.Def, remaining[0].StatType);
    }

    [Fact]
    public void GetAllActiveModifiers_FlattensModifiersFromAllActiveEffects()
    {
        var manager = new BuffManager();
        manager.ApplyEffect(MakeEffect("buff-a", 5f,
            new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 10 },
            new StatModifier { StatType = StatType.CritProb, ModType = ModifierType.PercentAdd, Value = 0.1 }));
        manager.ApplyEffect(MakeEffect("buff-b", 5f,
            new StatModifier { StatType = StatType.Def, ModType = ModifierType.FlatAdd, Value = 3 }));

        var all = manager.GetAllActiveModifiers();

        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void GetAllActiveModifiers_ReturnsEmpty_WhenNoEffectsApplied()
    {
        var manager = new BuffManager();

        Assert.Empty(manager.GetAllActiveModifiers());
    }
}
