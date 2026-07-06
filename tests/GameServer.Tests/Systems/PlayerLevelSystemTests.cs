using GameServer.Entities;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class PlayerLevelSystemTests
{
    private static Player MakePlayer()
    {
        return new Player { InstanceId = "p1", AccountId = 1 };
    }

    [Fact]
    public void ApplyLevel_SetsLevelAndBaseStats_AndRefreshesFinalStats()
    {
        var player = MakePlayer();

        PlayerLevelSystem.ApplyLevel(player, 3); // Lv3: Hp170, Atk19, Def5

        Assert.Equal(3, player.Level);
        Assert.Equal(170, player.BaseStats.Hp);
        Assert.Equal(19, player.BaseStats.Atk);
        Assert.Equal(5, player.BaseStats.Def);
        // UpdateFinalStats까지 호출됐는지 FinalStats로 확인(장비 없으므로 BaseStats와 동일해야 함)
        Assert.Equal(170, player.FinalStats.MaxHp);
        Assert.Equal(19, player.FinalStats.Atk);
    }

    [Fact]
    public void CheckLevelUp_ExpBelowNextThreshold_DoesNotLevelUp()
    {
        var player = MakePlayer();
        PlayerLevelSystem.ApplyLevel(player, 1); // 다음 레벨(2) 임계치 20
        player.AddExp(19);

        var leveledUp = PlayerLevelSystem.CheckLevelUp(player);

        Assert.False(leveledUp);
        Assert.Equal(1, player.Level);
    }

    [Fact]
    public void CheckLevelUp_ExpCrossesOneThreshold_LevelsUpOnce()
    {
        var player = MakePlayer();
        PlayerLevelSystem.ApplyLevel(player, 1);
        player.AddExp(20); // Lv2 임계치와 정확히 일치

        var leveledUp = PlayerLevelSystem.CheckLevelUp(player);

        Assert.True(leveledUp);
        Assert.Equal(2, player.Level);
        Assert.Equal(130, player.BaseStats.Hp); // Lv2 스탯 반영 확인
    }

    [Fact]
    public void CheckLevelUp_ExpCrossesMultipleThresholds_LevelsUpToHighestQualifying()
    {
        var player = MakePlayer();
        PlayerLevelSystem.ApplyLevel(player, 1);
        player.AddExp(400); // Lv6(300) 이상, Lv7(470) 미만 → Lv6까지만

        var leveledUp = PlayerLevelSystem.CheckLevelUp(player);

        Assert.True(leveledUp);
        Assert.Equal(6, player.Level);
    }

    [Fact]
    public void CheckLevelUp_AtMaxLevel_DoesNotExceedMaxLevel()
    {
        var player = MakePlayer();
        PlayerLevelSystem.ApplyLevel(player, 1);
        player.AddExp(999_999); // 테이블의 모든 임계치를 훨씬 초과

        PlayerLevelSystem.CheckLevelUp(player);

        Assert.Equal(LevelTable.MaxLevel, player.Level);
    }
}
