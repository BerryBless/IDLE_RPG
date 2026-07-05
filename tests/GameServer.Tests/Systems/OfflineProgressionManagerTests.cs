using GameServer.Entities;
using GameServer.Stats;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class OfflineProgressionManagerTests
{
    private static Player MakePlayer(double atk, double atkSpeed)
    {
        var player = new Player
        {
            InstanceId = "offline-player",
            AccountId = 1,
            Level = 1,
            BaseStats = new BaseStats { Atk = atk },
            BaseTraits = new Traits { AtkSpeed = atkSpeed }
        };
        player.UpdateFinalStats();
        return player;
    }

    private static Monster MakeMonster(double maxHp, double def, RewardComponent? rewards = null)
    {
        var monster = new Monster
        {
            InstanceId = "offline-monster",
            MonsterId = 1,
            Level = 1,
            BaseStats = new BaseStats { Hp = maxHp, Def = def },
            Rewards = rewards ?? new RewardComponent { ExpDrop = 1, GoldDrop = 0 }
        };
        monster.UpdateFinalStats();
        return monster;
    }

    [Fact]
    public void ProcessOfflineTime_ConvertsElapsedTimeIntoKillCount_ViaExpectedDps()
    {
        // Atk=100, AtkSpeed=2.0, Def=0/ArmorPen=0(무뎀감) → effectiveDps = 100*2.0*1 = 200
        var player = MakePlayer(atk: 100, atkSpeed: 2.0);
        var monster = MakeMonster(maxHp: 1000, def: 0, rewards: new RewardComponent { ExpDrop = 1, GoldDrop = 0 });
        var manager = new OfflineProgressionManager();

        // killCount = floor(100초 * 200dps / 1000hp) = floor(20) = 20
        var loot = manager.ProcessOfflineTime(player, monster, offlineSeconds: 100);

        Assert.Equal(20, loot.TotalExp);
    }

    [Fact]
    public void ProcessOfflineTime_MonsterTooTanky_ReturnsZeroKills()
    {
        var player = MakePlayer(atk: 1, atkSpeed: 1.0);
        var monster = MakeMonster(maxHp: 1_000_000, def: 0, rewards: new RewardComponent { ExpDrop = 1, GoldDrop = 1 });
        var manager = new OfflineProgressionManager();

        var loot = manager.ProcessOfflineTime(player, monster, offlineSeconds: 10);

        Assert.Equal(0, loot.TotalExp);
        Assert.Equal(0, loot.TotalGold);
        Assert.Empty(loot.AcquiredItems);
    }

    [Fact]
    public void ProcessOfflineTime_DelegatesToStageMonstersRewardComponent()
    {
        var player = MakePlayer(atk: 100, atkSpeed: 2.0);
        var rewards = new RewardComponent { ExpDrop = 3, GoldDrop = 7 };
        var monster = MakeMonster(maxHp: 200, def: 0, rewards: rewards);
        var manager = new OfflineProgressionManager();

        // killCount = floor(10초 * 200dps / 200hp) = floor(10) = 10
        var loot = manager.ProcessOfflineTime(player, monster, offlineSeconds: 10);

        Assert.Equal(30, loot.TotalExp);
        Assert.Equal(70, loot.TotalGold);
    }

    [Fact]
    public void ProcessOfflineTime_DefenseReducesEffectiveDps_LoweringKillCount()
    {
        var player = MakePlayer(atk: 100, atkSpeed: 2.0);
        // Def=100 → defMult = 100/(100+100) = 0.5 → effectiveDps = 200*0.5 = 100
        var monster = MakeMonster(maxHp: 1000, def: 100, rewards: new RewardComponent { ExpDrop = 1, GoldDrop = 0 });
        var manager = new OfflineProgressionManager();

        // killCount = floor(100초 * 100dps / 1000hp) = floor(10) = 10
        var loot = manager.ProcessOfflineTime(player, monster, offlineSeconds: 100);

        Assert.Equal(10, loot.TotalExp);
    }
}
