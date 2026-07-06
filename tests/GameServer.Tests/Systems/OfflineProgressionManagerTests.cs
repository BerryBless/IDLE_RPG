namespace GameServer.Tests.Systems;

// [2026-07-06 임시 주석 처리] GameServer/Systems/OfflineProgressionManager.cs가 같은 날짜에
// 블록 주석 처리되어 이 타입이 컴파일 대상에서 빠졌으므로, 이 테스트 파일도 함께 주석 처리한다
// (재활성화 시 함께 풀 것 — plan/battle_system_0705.md §6 참고).
/*
using GameServer.Entities;
using GameServer.Items;
using GameServer.Stats;
using GameServer.Systems;

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

    [Fact]
    public void ProcessOfflineTime_WeaponAttackScaling_ScalesKillCount()
    {
        // 코드리뷰 F1 회귀 테스트: 무기 AttackScaling이 오프라인 기대 DPS에도 반영되어야 한다.
        var player = MakePlayer(atk: 100, atkSpeed: 2.0);
        var weapon = new Weapon { InstanceId = "w1", ItemMetaId = 1, Name = "테스트 검", AttackScaling = 1.5f };
        player.Equipment.Equip(weapon, SlotType.Weapon);
        player.UpdateFinalStats(); // 무기 장착 반영을 위해 재계산

        var monster = MakeMonster(maxHp: 1000, def: 0, rewards: new RewardComponent { ExpDrop = 1, GoldDrop = 0 });
        var manager = new OfflineProgressionManager();

        // effectiveDps = 100 * 1.5(AttackScaling) * 2.0(AtkSpeed) * 1(무뎀감) = 300
        // killCount = floor(100초 * 300dps / 1000hp) = floor(30) = 30 (무기 없을 때의 20보다 커야 함)
        var loot = manager.ProcessOfflineTime(player, monster, offlineSeconds: 100);

        Assert.Equal(30, loot.TotalExp);
    }

    [Fact]
    public void ProcessOfflineTime_NegativeOfflineSeconds_ClampsToZeroKills()
    {
        // 코드리뷰 F2: 음수 경과시간(시계 오차 등)이 음수 killCount로 이어지지 않도록 0으로 클램프.
        var player = MakePlayer(atk: 100, atkSpeed: 2.0);
        var monster = MakeMonster(maxHp: 1000, def: 0, rewards: new RewardComponent { ExpDrop = 1, GoldDrop = 1 });
        var manager = new OfflineProgressionManager();

        var loot = manager.ProcessOfflineTime(player, monster, offlineSeconds: -50);

        Assert.Equal(0, loot.TotalExp);
        Assert.Equal(0, loot.TotalGold);
        Assert.Empty(loot.AcquiredItems);
    }
}
*/
