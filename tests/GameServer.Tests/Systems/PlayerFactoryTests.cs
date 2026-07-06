using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class PlayerFactoryTests
{
    private static readonly PlayerLevelSystem LevelSystem = PlayerLevelSystem.CreateDefault();

    [Fact]
    public void Create_SetsInstanceIdAndAccountId()
    {
        var player = PlayerFactory.Create("p-1", accountId: 42, level: 1, LevelSystem);

        Assert.Equal("p-1", player.InstanceId);
        Assert.Equal(42, player.AccountId);
    }

    [Fact]
    public void Create_AppliesLevelStats()
    {
        var player = PlayerFactory.Create("p-1", accountId: 1, level: 3, LevelSystem); // Lv3: Hp170, Atk19, Def5

        Assert.Equal(3, player.Level);
        Assert.Equal(170, player.BaseStats.Hp);
        Assert.Equal(19, player.BaseStats.Atk);
    }

    [Fact]
    public void Create_ReturnsPlayerImmediatelyAliveAtFullHp()
    {
        // 코드리뷰 2026-07-06 Medium 수정: RestoreResources 호출 누락 함정을 팩토리로 해소했는지 검증.
        var player = PlayerFactory.Create("p-1", accountId: 1, level: 1, LevelSystem);

        Assert.True(player.IsAlive);
        Assert.Equal(player.FinalStats.MaxHp, player.FinalStats.CurrentHp);
    }

    [Fact]
    public void Create_NullLevelSystem_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PlayerFactory.Create("p-1", accountId: 1, level: 1, null!));
    }
}
