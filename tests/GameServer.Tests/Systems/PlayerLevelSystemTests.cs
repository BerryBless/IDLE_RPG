using GameServer.Entities;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class PlayerLevelSystemTests
{
    // 인스턴스이지만 데이터가 불변이라 여러 테스트가 동시에 읽어도 안전하다.
    // (필드명을 System으로 하지 않는다 — System 네임스페이스와 혼동될 수 있음)
    private static readonly PlayerLevelSystem LevelSystem = PlayerLevelSystem.CreateDefault();

    private static Player MakePlayer()
    {
        return new Player { InstanceId = "p1", AccountId = 1 };
    }

    [Fact]
    public void ApplyLevel_SetsLevelAndBaseStats_AndRefreshesFinalStats()
    {
        var player = MakePlayer();

        LevelSystem.ApplyLevel(player, 3); // Lv3: Hp170, Atk19, Def5

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
        LevelSystem.ApplyLevel(player, 1); // 다음 레벨(2) 임계치 20
        player.AddExp(19);

        var leveledUp = LevelSystem.CheckLevelUp(player);

        Assert.False(leveledUp);
        Assert.Equal(1, player.Level);
    }

    [Fact]
    public void CheckLevelUp_ExpCrossesOneThreshold_LevelsUpOnce()
    {
        var player = MakePlayer();
        LevelSystem.ApplyLevel(player, 1);
        player.AddExp(20); // Lv2 임계치와 정확히 일치

        var leveledUp = LevelSystem.CheckLevelUp(player);

        Assert.True(leveledUp);
        Assert.Equal(2, player.Level);
        Assert.Equal(130, player.BaseStats.Hp); // Lv2 스탯 반영 확인
    }

    [Fact]
    public void CheckLevelUp_ExpCrossesMultipleThresholds_LevelsUpToHighestQualifying()
    {
        var player = MakePlayer();
        LevelSystem.ApplyLevel(player, 1);
        player.AddExp(400); // Lv6(300) 이상, Lv7(470) 미만 → Lv6까지만

        var leveledUp = LevelSystem.CheckLevelUp(player);

        Assert.True(leveledUp);
        Assert.Equal(6, player.Level);
    }

    [Fact]
    public void CheckLevelUp_AtMaxLevel_DoesNotExceedMaxLevel()
    {
        var player = MakePlayer();
        LevelSystem.ApplyLevel(player, 1);
        player.AddExp(999_999); // 테이블의 모든 임계치를 훨씬 초과

        LevelSystem.CheckLevelUp(player);

        Assert.Equal(LevelTable.CreateDefault().MaxLevel, player.Level);
    }

    [Fact]
    public void Constructor_AcceptsCustomLevelTable_IndependentOfDefaultSystem()
    {
        // 코드리뷰 H1: 레벨 규칙 자체를 교체할 수 있어야 한다는 DIP 목적을 직접 검증.
        var customTable = new LevelTable(new List<LevelTemplate>
        {
            new() { Level = 1, RequiredExp = 0, Hp = 1, Atk = 1, Def = 0 },
            new() { Level = 2, RequiredExp = 5, Hp = 2, Atk = 2, Def = 0 }
        });
        var customSystem = new PlayerLevelSystem(customTable);
        var player = MakePlayer();
        customSystem.ApplyLevel(player, 1);
        player.AddExp(5);

        var leveledUp = customSystem.CheckLevelUp(player);

        Assert.True(leveledUp);
        Assert.Equal(2, player.Level); // 커스텀 테이블 기준 최고 레벨(2)에서 정지
    }
}
