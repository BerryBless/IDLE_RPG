using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class MonsterTableTests
{
    [Fact]
    public void All_ContainsExactlyTenMonsters()
    {
        Assert.Equal(10, MonsterTable.All.Count);
    }

    [Fact]
    public void All_MonsterIdsAreUnique()
    {
        var distinctIds = MonsterTable.All.Select(m => m.MonsterId).Distinct().Count();

        Assert.Equal(MonsterTable.All.Count, distinctIds);
    }

    [Fact]
    public void GetById_KnownId_ReturnsMatchingTemplate()
    {
        var template = MonsterTable.GetById(2001);

        Assert.Equal(2001, template.MonsterId);
        Assert.Equal("슬라임", template.Name);
    }

    [Fact]
    public void GetById_UnknownId_ThrowsKeyNotFoundException()
    {
        Assert.Throws<KeyNotFoundException>(() => MonsterTable.GetById(999999));
    }
}
