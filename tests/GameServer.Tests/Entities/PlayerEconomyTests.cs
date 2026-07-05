using GameServer.Entities;

namespace GameServer.Tests.Entities;

public class PlayerEconomyTests
{
    private static Player MakePlayer() => new() { InstanceId = "player-eco", AccountId = 1, Level = 1 };

    [Fact]
    public void AddExp_AccumulatesIntoCurrentExp()
    {
        var player = MakePlayer();

        player.AddExp(100);
        player.AddExp(50);

        Assert.Equal(150, player.CurrentExp);
    }

    [Fact]
    public void AddGold_AccumulatesIntoCurrentGold()
    {
        var player = MakePlayer();

        player.AddGold(20);
        player.AddGold(5);

        Assert.Equal(25, player.CurrentGold);
    }
}
