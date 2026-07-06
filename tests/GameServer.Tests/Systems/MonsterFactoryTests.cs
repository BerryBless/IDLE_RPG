using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class MonsterFactoryTests
{
    [Fact]
    public void CreateMonster_ReflectsTemplateStatsInFinalStats()
    {
        var template = MonsterTable.GetById(2003); // 고블린: Hp 55, Atk 8, Def 2

        var monster = MonsterFactory.CreateMonster(template);

        Assert.Equal(2003, monster.MonsterId);
        Assert.Equal(55, monster.FinalStats.MaxHp);
        Assert.Equal(8, monster.FinalStats.Atk);
        Assert.Equal(2, monster.FinalStats.Def);
    }

    [Fact]
    public void CreateMonster_IsImmediatelyAliveAtFullHp()
    {
        // RestoreResources()까지 호출된 상태로 반환되어야 바로 전투에 투입 가능하다.
        var template = MonsterTable.GetById(2001);

        var monster = MonsterFactory.CreateMonster(template);

        Assert.True(monster.IsAlive);
        Assert.Equal(monster.FinalStats.MaxHp, monster.FinalStats.CurrentHp);
    }

    [Fact]
    public void CreateMonster_WiresRewardsFromTemplate()
    {
        var template = MonsterTable.GetById(2001);

        var monster = MonsterFactory.CreateMonster(template);
        var loot = monster.Rewards.GenerateLoot(1);

        Assert.Equal(template.ExpDrop, loot.TotalExp);
        Assert.Equal(template.GoldDrop, loot.TotalGold);
    }

    [Fact]
    public void CreateMonster_WiresAffixesFromTemplate()
    {
        // 스켈레톤(2006)은 ArmorPen 어픽스를 갖도록 설계됨 — Traits 집계에 반영되는지 확인.
        var template = MonsterTable.GetById(2006);

        var monster = MonsterFactory.CreateMonster(template);

        Assert.NotEmpty(template.Affixes);
        Assert.True(monster.FinalStats.CombatTraits.ArmorPen > 0);
    }

    [Fact]
    public void CreateMonster_EachCallProducesIndependentInstance()
    {
        var template = MonsterTable.GetById(2001);

        var first = MonsterFactory.CreateMonster(template);
        var second = MonsterFactory.CreateMonster(template);

        Assert.NotSame(first, second);
        Assert.NotEqual(first.InstanceId, second.InstanceId);
    }
}
