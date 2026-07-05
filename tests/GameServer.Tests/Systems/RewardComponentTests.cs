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
    public void GenerateLoot_GuaranteedDrop_AggregatesQuantityIntoOneItemPerDistinctItemMetaId()
    {
        // 코드리뷰 F11: kill마다 개별 LootItem을 만드는 대신 ItemMetaId별로 수량을 합산해 반환한다.
        // Sample()=0.0 → NextDouble()=0.0 → 어떤 양수 DropChance와 비교해도 항상 드롭.
        var reward = new RewardComponent(new FixedRandom(0.0))
        {
            DropTable = [new DropPool { ItemMetaId = 100, DropChance = 0.5f, MinQty = 2, MaxQty = 2 }]
        };

        var loot = reward.GenerateLoot(3);

        var item = Assert.Single(loot.AcquiredItems); // 3킬 모두 같은 아이템 → 1개로 합산
        Assert.Equal(100, item.ItemMetaId);
        Assert.Equal(6, Assert.IsType<LootItem>(item).Quantity); // 2개씩 3킬 = 6
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

        // 20킬 각각 [1,5] 수량이 합산되므로, 최종 합계는 [20,100] 범위에 있어야 한다.
        var item = Assert.Single(loot.AcquiredItems);
        var totalQty = Assert.IsType<LootItem>(item).Quantity;
        Assert.InRange(totalQty, 20, 100);
    }

    [Fact]
    public void GenerateLoot_LargeKillCount_AllocationBoundedByDropTableSize()
    {
        // 코드리뷰 F11: 할당량이 killCount가 아니라 DropTable 크기에 비례해야 한다
        // (오프라인 장시간 방치로 killCount가 매우 커져도 AcquiredItems는 distinct 아이템 수만큼만 생성).
        var reward = new RewardComponent
        {
            DropTable =
            [
                new DropPool { ItemMetaId = 100, DropChance = 1.0f, MinQty = 1, MaxQty = 1 },
                new DropPool { ItemMetaId = 200, DropChance = 1.0f, MinQty = 1, MaxQty = 1 }
            ]
        };

        var loot = reward.GenerateLoot(100_000);

        Assert.Equal(2, loot.AcquiredItems.Count); // killCount(10만)가 아니라 DropTable 크기(2)만큼만
        Assert.Contains(loot.AcquiredItems, i => i.ItemMetaId == 100 && ((LootItem)i).Quantity == 100_000);
        Assert.Contains(loot.AcquiredItems, i => i.ItemMetaId == 200 && ((LootItem)i).Quantity == 100_000);
    }

    [Fact]
    public void GenerateLoot_NegativeKillCount_ClampsToZero_ReturnsEmptyLoot()
    {
        // 코드리뷰 F3: 음수 killCount(예: F2의 음수 offlineSeconds에서 흘러들어온 값)가
        // 음수 경험치/골드를 반환하지 않도록 방어.
        var reward = new RewardComponent
        {
            ExpDrop = 1,
            GoldDrop = 1,
            DropTable = [new DropPool { ItemMetaId = 100, DropChance = 1.0f, MinQty = 1, MaxQty = 1 }]
        };

        var loot = reward.GenerateLoot(-5);

        Assert.Equal(0, loot.TotalExp);
        Assert.Equal(0, loot.TotalGold);
        Assert.Empty(loot.AcquiredItems);
    }

    [Fact]
    public void GenerateLoot_MalformedDropPool_MinQtyGreaterThanMaxQty_DoesNotThrow()
    {
        // 코드리뷰 F4: 마스터 데이터 오류(MinQty > MaxQty)로 인한 ArgumentOutOfRangeException 방지.
        var reward = new RewardComponent(new FixedRandom(0.0))
        {
            DropTable = [new DropPool { ItemMetaId = 100, DropChance = 1.0f, MinQty = 5, MaxQty = 3 }]
        };

        var loot = reward.GenerateLoot(1);

        var item = Assert.Single(loot.AcquiredItems);
        Assert.Equal(5, Assert.IsType<LootItem>(item).Quantity);
    }
}
