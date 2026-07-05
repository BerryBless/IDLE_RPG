using GameServer.Combat;
using GameServer.Stats;

namespace GameServer.Tests.Combat;

public class StatusEffectTests
{
    [Fact]
    public void Tick_DecreasesTimeRemainingByDeltaTime()
    {
        var effect = new StatusEffect { EffectId = "buff-atk", MaxDuration = 5f, TimeRemaining = 5f };

        effect.Tick(1.5f);

        Assert.Equal(3.5f, effect.TimeRemaining, precision: 5);
    }

    [Fact]
    public void IsExpired_ReturnsFalse_WhenTimeRemainingIsPositive()
    {
        var effect = new StatusEffect { EffectId = "buff-atk", MaxDuration = 5f, TimeRemaining = 0.1f };

        Assert.False(effect.IsExpired());
    }

    [Fact]
    public void IsExpired_ReturnsTrue_WhenTimeRemainingIsExactlyZero()
    {
        var effect = new StatusEffect { EffectId = "buff-atk", MaxDuration = 5f, TimeRemaining = 0f };

        Assert.True(effect.IsExpired());
    }

    [Fact]
    public void IsExpired_ReturnsTrue_WhenTimeRemainingIsNegative()
    {
        var effect = new StatusEffect { EffectId = "buff-atk", MaxDuration = 5f, TimeRemaining = -1f };

        Assert.True(effect.IsExpired());
    }

    [Fact]
    public void GetModifiers_ReturnsConfiguredModifiers()
    {
        var modifier = new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 10 };
        var effect = new StatusEffect
        {
            EffectId = "buff-atk",
            MaxDuration = 5f,
            TimeRemaining = 5f,
            Modifiers = [modifier]
        };

        var result = effect.GetModifiers();

        Assert.Single(result);
        Assert.Equal(modifier, result[0]);
    }
}
