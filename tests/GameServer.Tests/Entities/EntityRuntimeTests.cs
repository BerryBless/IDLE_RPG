using GameServer.Combat;
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

    [Fact]
    public void TakeDamage_NegativeAmount_DoesNotHealEntity()
    {
        // 코드리뷰 F5: 음수 피해량이 회복으로 작동하지 않도록 방어.
        var player = MakeReadyPlayer();
        player.TakeDamage(30); // CurrentHp = 70

        player.TakeDamage(-1000);

        Assert.Equal(70, player.FinalStats.CurrentHp);
    }

    [Fact]
    public void TryConsumeMana_NegativeAmount_FailsAndDoesNotGrantMana()
    {
        // 코드리뷰 F5: 음수 소모량이 마나 증가로 작동하지 않도록 방어.
        var player = MakeReadyPlayer();

        var result = player.TryConsumeMana(-100);

        Assert.False(result);
        Assert.Equal(50, player.FinalStats.CurrentMana);
    }

    [Fact]
    public void IsAlive_ReflectsCurrentHp()
    {
        var player = MakeReadyPlayer();
        Assert.True(player.IsAlive);

        player.TakeDamage(9999);

        Assert.False(player.IsAlive);
    }

    [Fact]
    public void Update_DeadEntity_DoesNotRegenerateHpOrMana()
    {
        // 코드리뷰 F6: 사망한 개체는 회복/마나재생이 멈춰야 한다.
        var player = MakeReadyPlayer();
        player.TakeDamage(9999); // CurrentHp = 0

        player.Update(10f); // Recovery=5, ManaRegen=10이 살아있었다면 크게 회복했을 시간

        Assert.Equal(0, player.FinalStats.CurrentHp);
        Assert.Equal(50, player.FinalStats.CurrentMana); // RestoreResources 시점 그대로, 재생 없음
    }

    [Fact]
    public void Update_DeadEntity_DoesNotTickBuffs()
    {
        // 코드리뷰 F6: 사망한 개체는 버프 만료 처리도 멈춰야 한다(전체 조기 리턴).
        var player = MakeReadyPlayer();
        var effect = new StatusEffect { EffectId = "buff", MaxDuration = 1f, TimeRemaining = 1f };
        player.BuffManager.ApplyEffect(effect);
        player.TakeDamage(9999); // CurrentHp = 0

        player.Update(5f); // 살아있었다면 버프가 만료되고도 남을 시간

        Assert.Equal(1f, effect.TimeRemaining, precision: 5);
    }

    [Fact]
    public void Update_AliveEntity_StillRegeneratesAsBefore()
    {
        // 회귀 확인: 조기 리턴 가드가 생존 개체의 기존 동작을 건드리지 않는지.
        var player = MakeReadyPlayer();
        player.TakeDamage(50); // CurrentHp = 50

        player.Update(1f);

        Assert.Equal(55, player.FinalStats.CurrentHp);
    }
}
