using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class LevelTableTests
{
    [Fact]
    public void All_ContainsExactlyTenLevels()
    {
        Assert.Equal(10, LevelTable.All.Count);
    }

    [Fact]
    public void All_LevelsAreSequentialStartingFromOne()
    {
        for (var i = 0; i < LevelTable.All.Count; i++)
        {
            Assert.Equal(i + 1, LevelTable.All[i].Level);
        }
    }

    [Fact]
    public void All_RequiredExpIsStrictlyIncreasing()
    {
        for (var i = 1; i < LevelTable.All.Count; i++)
        {
            Assert.True(LevelTable.All[i].RequiredExp > LevelTable.All[i - 1].RequiredExp);
        }
    }

    [Fact]
    public void MaxLevel_EqualsTableCount()
    {
        Assert.Equal(10, LevelTable.MaxLevel);
    }

    [Fact]
    public void GetByLevel_KnownLevel_ReturnsMatchingTemplate()
    {
        var template = LevelTable.GetByLevel(1);

        Assert.Equal(1, template.Level);
        Assert.Equal(0, template.RequiredExp);
        Assert.Equal(100, template.Hp);
    }

    [Fact]
    public void GetByLevel_UnknownLevel_ThrowsKeyNotFoundException()
    {
        Assert.Throws<KeyNotFoundException>(() => LevelTable.GetByLevel(999));
    }
}
