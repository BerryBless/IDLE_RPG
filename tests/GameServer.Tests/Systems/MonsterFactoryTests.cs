using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class MonsterFactoryTests
{
    // 인스턴스이지만 데이터가 불변(생성자에서만 설정)이라 여러 테스트가 동시에 읽어도 안전하다.
    private static readonly MonsterTable Table = MonsterTable.CreateDefault();

    [Fact]
    public void Create_ReflectsTemplateStatsInFinalStats()
    {
        var template = Table.GetById(2003); // 고블린: Hp 55, Atk 8, Def 2

        var monster = MonsterFactory.Create(template);

        Assert.Equal(2003, monster.MonsterId);
        Assert.Equal(55, monster.FinalStats.MaxHp);
        Assert.Equal(8, monster.FinalStats.Atk);
        Assert.Equal(2, monster.FinalStats.Def);
    }

    [Fact]
    public void Create_IsImmediatelyAliveAtFullHp()
    {
        // RestoreResources()까지 호출된 상태로 반환되어야 바로 전투에 투입 가능하다.
        var template = Table.GetById(2001);

        var monster = MonsterFactory.Create(template);

        Assert.True(monster.IsAlive);
        Assert.Equal(monster.FinalStats.MaxHp, monster.FinalStats.CurrentHp);
    }

    [Fact]
    public void Create_WiresRewardsFromTemplate()
    {
        var template = Table.GetById(2001);

        var monster = MonsterFactory.Create(template);
        var loot = monster.Rewards.GenerateLoot(1);

        Assert.Equal(template.ExpDrop, loot.TotalExp);
        Assert.Equal(template.GoldDrop, loot.TotalGold);
    }

    [Fact]
    public void Create_WiresAffixesFromTemplate()
    {
        // 스켈레톤(2006)은 ArmorPen 어픽스를 갖도록 설계됨 — Traits 집계에 반영되는지 확인.
        var template = Table.GetById(2006);

        var monster = MonsterFactory.Create(template);

        Assert.NotEmpty(template.Affixes);
        Assert.True(monster.FinalStats.CombatTraits.ArmorPen > 0);
    }

    [Fact]
    public void Create_EachCallProducesIndependentInstance()
    {
        var template = Table.GetById(2001);

        var first = MonsterFactory.Create(template);
        var second = MonsterFactory.Create(template);

        Assert.NotSame(first, second);
        Assert.NotEqual(first.InstanceId, second.InstanceId);
    }
}
