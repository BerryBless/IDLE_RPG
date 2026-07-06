using GameServer.Entities;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

/// <summary>
/// BattleLoop.Tick()은 BattleManager.Instance의 공유 Random에 의존하지만, 테스트 엔티티는
/// BaseTraits(CritProb 등)를 전혀 설정하지 않아 기본값 0이므로 크리티컬이 결코 발동하지 않는다.
/// 따라서 아래 수치들은 크리 여부와 무관하게 항상 결정적이다.
/// </summary>
public class BattleLoopTests
{
    private static Player MakePlayer(double hp, double atk)
    {
        var player = new Player { InstanceId = "p1", AccountId = 1, Level = 1 };
        player.BaseStats.Hp = hp;
        player.BaseStats.Atk = atk;
        player.UpdateFinalStats();
        player.RestoreResources();
        return player;
    }

    private static Monster MakeMonster(double hp, double atk)
    {
        var monster = new Monster
        {
            InstanceId = "m1",
            MonsterId = 1,
            Level = 1,
            Rewards = new RewardComponent { ExpDrop = 10, GoldDrop = 5 }
        };
        monster.BaseStats.Hp = hp;
        monster.BaseStats.Atk = atk;
        monster.UpdateFinalStats();
        monster.RestoreResources();
        return monster;
    }

    [Fact]
    public void Tick_MonsterDefeated_GrantsRewardsAndRespawnsFullHp()
    {
        // 플레이어 Atk(1000)가 몬스터 HP(10)를 훨씬 초과 — 확정 처치.
        // 몬스터 Atk=0이라 이번 틱에 반격이 있더라도 플레이어에게 영향 없음(사망 시 반격 자체가 없어야 함).
        var player = MakePlayer(hp: 100, atk: 1000);
        var monster = MakeMonster(hp: 10, atk: 0);
        var loop = new BattleLoop();

        var result = loop.Tick(player, monster, deltaTime: 1f);

        Assert.Equal(BattleTickEvent.MonsterDefeated, result);
        Assert.Equal(10, player.CurrentExp);
        Assert.Equal(5, player.CurrentGold);
        Assert.Equal(monster.FinalStats.MaxHp, monster.FinalStats.CurrentHp); // 재등장 시 풀피
        Assert.Equal(player.FinalStats.MaxHp, player.FinalStats.CurrentHp); // 반격 없었음 확인
    }

    [Fact]
    public void Tick_NeitherDies_BothTakeExactDamage()
    {
        // 양쪽 모두 죽지 않을 만큼 HP를 크게 잡아 정상 교환 1회를 결정적으로 검증한다.
        var player = MakePlayer(hp: 100, atk: 1);
        var monster = MakeMonster(hp: 1000, atk: 20);
        var loop = new BattleLoop();

        var result = loop.Tick(player, monster, deltaTime: 1f);

        Assert.Equal(BattleTickEvent.None, result);
        Assert.Equal(999, monster.FinalStats.CurrentHp); // Def=0, ArmorPen=0 → defMult=1 → 피해 1 그대로
        Assert.Equal(80, player.FinalStats.CurrentHp); // 20 피해
    }

    [Fact]
    public void Tick_PlayerDefeated_RevivesImmediatelyToFullHp()
    {
        // 플레이어 공격으로는 몬스터가 죽지 않아 몬스터가 반드시 반격하고, 그 반격이 플레이어 HP를 초과.
        var player = MakePlayer(hp: 50, atk: 1);
        var monster = MakeMonster(hp: 1000, atk: 9999);
        var loop = new BattleLoop();

        var result = loop.Tick(player, monster, deltaTime: 1f);

        Assert.Equal(BattleTickEvent.PlayerDefeated, result);
        Assert.Equal(player.FinalStats.MaxHp, player.FinalStats.CurrentHp); // 즉시 부활 확인
    }

    [Fact]
    public void Tick_MonsterDefeated_ExpCrossesLevelThreshold_LevelsUpPlayer()
    {
        // 코드리뷰: 레벨 테이블 배선 확인. 몬스터 ExpDrop=25는 Lv2 임계치(20)를 넘으므로
        // 한 번의 처치로 플레이어가 Lv1→Lv2로 레벨업해야 한다.
        var player = MakePlayer(hp: 100, atk: 1000);
        var monster = new Monster
        {
            InstanceId = "m-levelup",
            MonsterId = 1,
            Level = 1,
            Rewards = new RewardComponent { ExpDrop = 25, GoldDrop = 0 }
        };
        monster.BaseStats.Hp = 10;
        monster.BaseStats.Atk = 0;
        monster.UpdateFinalStats();
        monster.RestoreResources();
        var loop = new BattleLoop();

        var result = loop.Tick(player, monster, deltaTime: 1f);

        Assert.Equal(BattleTickEvent.MonsterDefeated, result);
        Assert.Equal(2, player.Level);
        Assert.Equal(130, player.BaseStats.Hp); // LevelTable Lv2 스탯 반영 확인
    }

    [Fact]
    public async Task RunAsync_WithCancellationToken_StopsWithoutHanging()
    {
        // HP를 충분히 크게 잡아 취소 시점까지 어느 쪽도 죽지 않게 하고, 취소 후 정상 반환되는지만 확인.
        var player = MakePlayer(hp: 1_000_000, atk: 1);
        var monster = MakeMonster(hp: 1_000_000, atk: 1);
        var loop = new BattleLoop();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await loop.RunAsync(player, monster, TimeSpan.FromMilliseconds(1), cts.Token);

        // 취소 후 정상 반환된 것 자체가 핵심 검증(무한 루프에 갇히지 않음, 스레드도 점유하지 않음 — 코드리뷰 H2).
        // 20ms 동안 1ms 간격이면 최소 한 틱은 진행되어 양쪽 다 약간의 피해를 입었을 것이다.
        Assert.True(monster.FinalStats.CurrentHp < monster.FinalStats.MaxHp);
        Assert.True(player.FinalStats.CurrentHp < player.FinalStats.MaxHp);
    }
}
