using GameServer.Entities;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class ShardBattleRunnerTests
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
    public void TryTick_NormalExchange_ReturnsEventAndNullException()
    {
        var player = MakePlayer(hp: 100, atk: 1000);
        var monster = MakeMonster(hp: 10, atk: 0);
        var loop = new BattleLoop();

        var result = ShardBattleRunner.TryTick(loop, player, monster, deltaTime: 1f, out var exception);

        Assert.Equal(BattleTickEvent.MonsterDefeated, result);
        Assert.Null(exception);
    }

    [Fact]
    public void TryTick_TickThrows_CatchesExceptionAndReturnsNull()
    {
        // Rewards를 null로 만들면 몬스터 처치 시 BattleLoop.Tick 내부의
        // monster.Rewards.GenerateLoot(1) 호출에서 NullReferenceException이 발생한다 —
        // 쌍 단위 예외 격리를 결정적으로 검증하기 위한 인위적 결함 주입.
        // (MakeMonster를 쓰지 않고 직접 생성 — MakeMonster는 항상 유효한 Rewards를 채워서
        // null을 넘길 방법이 없다.)
        var player = MakePlayer(hp: 100, atk: 1000);
        var monster = new Monster
        {
            InstanceId = "m1",
            MonsterId = 1,
            Level = 1,
            Rewards = null!
        };
        monster.BaseStats.Hp = 10;
        monster.BaseStats.Atk = 0;
        monster.UpdateFinalStats();
        monster.RestoreResources();
        var loop = new BattleLoop();

        var result = ShardBattleRunner.TryTick(loop, player, monster, deltaTime: 1f, out var exception);

        Assert.Null(result);
        Assert.NotNull(exception);
        Assert.IsType<NullReferenceException>(exception);
    }
}
