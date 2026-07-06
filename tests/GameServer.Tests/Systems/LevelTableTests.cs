using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class LevelTableTests
{
    [Fact]
    public void All_ContainsExactlyTenLevels()
    {
        var table = LevelTable.CreateDefault();

        Assert.Equal(10, table.All.Count);
    }

    [Fact]
    public void All_LevelsAreSequentialStartingFromOne()
    {
        var table = LevelTable.CreateDefault();

        for (var i = 0; i < table.All.Count; i++)
        {
            Assert.Equal(i + 1, table.All[i].Level);
        }
    }

    [Fact]
    public void All_RequiredExpIsStrictlyIncreasing()
    {
        var table = LevelTable.CreateDefault();

        for (var i = 1; i < table.All.Count; i++)
        {
            Assert.True(table.All[i].RequiredExp > table.All[i - 1].RequiredExp);
        }
    }

    [Fact]
    public void MaxLevel_EqualsTableCount()
    {
        var table = LevelTable.CreateDefault();

        Assert.Equal(10, table.MaxLevel);
    }

    [Fact]
    public void MaxLevel_WithGapInLevels_ReturnsHighestLevelNotCount()
    {
        // 코드리뷰 부수 수정: MaxLevel이 개수(Count)가 아니라 실제 최고 Level 값을 반영해야 한다.
        // (1, 2, 5) 3개 항목이지만 최고 레벨은 5여야 한다.
        var table = new LevelTable(new List<LevelTemplate>
        {
            new() { Level = 1, RequiredExp = 0, Hp = 10, Atk = 1, Def = 0 },
            new() { Level = 2, RequiredExp = 10, Hp = 20, Atk = 2, Def = 0 },
            new() { Level = 5, RequiredExp = 50, Hp = 50, Atk = 5, Def = 0 }
        });

        Assert.Equal(5, table.MaxLevel);
        Assert.NotEqual(table.All.Count, table.MaxLevel);
    }

    [Fact]
    public void GetById_KnownLevel_ReturnsMatchingTemplate()
    {
        var table = LevelTable.CreateDefault();
        var template = table.GetById(1);

        Assert.Equal(1, template.Level);
        Assert.Equal(0, template.RequiredExp);
        Assert.Equal(100, template.Hp);
    }

    [Fact]
    public void GetById_UnknownLevel_ThrowsKeyNotFoundException()
    {
        var table = LevelTable.CreateDefault();

        Assert.Throws<KeyNotFoundException>(() => table.GetById(999));
    }
}
