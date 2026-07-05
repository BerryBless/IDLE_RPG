using GameServer.Entities;
using GameServer.Stats;

namespace GameServer.Tests.Entities;

/// <summary>Entity의 피해·회복·마나 소모 등 런타임 동작(TakeDamage/Update/TryConsumeMana/RestoreResources)을 검증한다.</summary>
public class EntityRuntimeTests
{
    private static Player MakeReadyPlayer(BaseStats? baseStats = null)
    {
        var player = new Player
        {
            InstanceId = "player-test",
            AccountId = 1,
            Level = 1,
            BaseStats = baseStats ?? new BaseStats { Hp = 100, Atk = 10, Def = 5, Recovery = 5, Mana = 50, ManaRegen = 10 }
        };
        player.UpdateFinalStats();
        player.RestoreResources();
        return player;
    }

    [Fact]
    public void RestoreResources_FillsCurrentHpAndManaToMax()
    {
        var player = MakeReadyPlayer();

        Assert.Equal(100, player.FinalStats.CurrentHp);
        Assert.Equal(50, player.FinalStats.CurrentMana);
    }

    [Fact]
    public void TakeDamage_ReducesCurrentHp()
    {
        var player = MakeReadyPlayer();

        player.TakeDamage(30);

        Assert.Equal(70, player.FinalStats.CurrentHp);
    }

    [Fact]
    public void TakeDamage_ClampsAtZero_WhenDamageExceedsCurrentHp()
    {
        var player = MakeReadyPlayer();

        player.TakeDamage(9999);

        Assert.Equal(0, player.FinalStats.CurrentHp);
    }

    [Fact]
    public void Update_RegeneratesHpByRecoveryTimesDeltaTime_ClampedAtMax()
    {
        var player = MakeReadyPlayer();
        player.TakeDamage(50); // CurrentHp = 50

        player.Update(1f); // Recovery=5 → +5

        Assert.Equal(55, player.FinalStats.CurrentHp);
    }

    [Fact]
    public void Update_DoesNotOverhealPastMaxHp()
    {
        var player = MakeReadyPlayer();
        player.TakeDamage(1); // CurrentHp = 99

        player.Update(10f); // Recovery=5 * 10 = 50 초과분은 클램프

        Assert.Equal(100, player.FinalStats.CurrentHp);
    }

    [Fact]
    public void Update_RegeneratesManaByManaRegenTimesDeltaTime_ClampedAtMax()
    {
        var player = MakeReadyPlayer();
        player.TryConsumeMana(30); // CurrentMana = 20

        player.Update(1f); // ManaRegen=10 → +10

        Assert.Equal(30, player.FinalStats.CurrentMana);
    }

    [Fact]
    public void TryConsumeMana_SucceedsAndDeductsWhenSufficient()
    {
        var player = MakeReadyPlayer();

        var result = player.TryConsumeMana(20);

        Assert.True(result);
        Assert.Equal(30, player.FinalStats.CurrentMana);
    }

    [Fact]
    public void TryConsumeMana_FailsAndLeavesManaUnchangedWhenInsufficient()
    {
        var player = MakeReadyPlayer();

        var result = player.TryConsumeMana(999);

        Assert.False(result);
        Assert.Equal(50, player.FinalStats.CurrentMana);
    }
}
