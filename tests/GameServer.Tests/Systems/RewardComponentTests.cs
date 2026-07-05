using GameServer.Systems;

namespace GameServer.Tests.Systems;

/// <summary>
/// 결정적 결과가 필요한 경우, Sample()을 오버라이드해 NextDouble()/Next()의 내부 난수를 고정하는 테스트 전용 Random.
/// System.Random은 파생 타입이 Sample()을 오버라이드하면 레거시(가상 디스패치 기반) 알고리즘으로 전환되어
/// NextDouble()/Next(min,max) 모두 이 값을 기준으로 동작한다(BattleManager의 internal Random 주입과 동일한 결정론 확보 패턴).
/// </summary>
internal sealed class FixedRandom(double sample) : Random
{
    protected override double Sample() => sample;
}

public class RewardComponentTests
{
    [Fact]
    public void GenerateLoot_ScalesExpAndGoldByKillCount()
    {
        var reward = new RewardComponent { ExpDrop = 2, GoldDrop = 5 };

        var loot = reward.GenerateLoot(3);

        Assert.Equal(6, loot.TotalExp);
        Assert.Equal(15, loot.TotalGold);
    }

    [Fact]
    public void GenerateLoot_WithEmptyDropTable_ReturnsNoItems()
    {
        var reward = new RewardComponent { ExpDrop = 1, GoldDrop = 1 };

        var loot = reward.GenerateLoot(5);

        Assert.Empty(loot.AcquiredItems);
    }

    [Fact]
    public void GenerateLoot_GuaranteedDrop_AddsOneItemPerKill()
    {
        // Sample()=0.0 → NextDouble()=0.0 → 어떤 양수 DropChance와 비교해도 항상 드롭.
        var reward = new RewardComponent(new FixedRandom(0.0))
        {
            DropTable = [new DropPool { ItemMetaId = 100, DropChance = 0.5f, MinQty = 2, MaxQty = 2 }]
        };

        var loot = reward.GenerateLoot(3);

        Assert.Equal(3, loot.AcquiredItems.Count);
        Assert.All(loot.AcquiredItems, item =>
        {
            Assert.Equal(100, item.ItemMetaId);
            Assert.Equal(2, Assert.IsType<LootItem>(item).Quantity);
        });
    }

    [Fact]
    public void GenerateLoot_ChanceNeverMet_AddsNoItems()
    {
        // Sample()이 1.0에 매우 가까움 → NextDouble() >= DropChance(0.1) → 절대 드롭 안 함.
        var reward = new RewardComponent(new FixedRandom(0.999999))
        {
            DropTable = [new DropPool { ItemMetaId = 100, DropChance = 0.1f, MinQty = 1, MaxQty = 1 }]
        };

        var loot = reward.GenerateLoot(10);

        Assert.Empty(loot.AcquiredItems);
    }

    [Fact]
    public void GenerateLoot_QuantityStaysWithinConfiguredRange()
    {
        var reward = new RewardComponent
        {
            DropTable = [new DropPool { ItemMetaId = 100, DropChance = 1.0f, MinQty = 1, MaxQty = 5 }]
        };

        var loot = reward.GenerateLoot(20);

        Assert.Equal(20, loot.AcquiredItems.Count);
        Assert.All(loot.AcquiredItems, item =>
        {
            var qty = Assert.IsType<LootItem>(item).Quantity;
            Assert.InRange(qty, 1, 5);
        });
    }
}
