using GameServer.Entities;
using GameServer.Stats;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class BattleEventLoggerTests
{
    private static Player MakePlayer(int level, BigNumber exp, BigNumber gold) =>
        new() { InstanceId = "unused", AccountId = 1, Level = level, CurrentExp = exp, CurrentGold = gold };

    [Fact]
    public void Format_MonsterDefeated_IncludesInstanceIdLevelExpAndGold()
    {
        var player = MakePlayer(level: 3, exp: 41, gold: 22);

        var log = BattleEventLogger.Format("player-0042", BattleTickEvent.MonsterDefeated, player);

        Assert.Equal("[player-0042] [처치] 몬스터 처치! Lv.3 누적 Exp=41, Gold=22", log);
    }

    [Fact]
    public void Format_PlayerDefeated_IncludesInstanceIdOnly()
    {
        var player = MakePlayer(level: 1, exp: 0, gold: 0);

        var log = BattleEventLogger.Format("player-0187", BattleTickEvent.PlayerDefeated, player);

        Assert.Equal("[player-0187] [부활] 플레이어 사망 → 즉시 부활", log);
    }

    [Fact]
    public void Format_NoneEvent_ReturnsEmptyString()
    {
        var player = MakePlayer(level: 1, exp: 0, gold: 0);

        var log = BattleEventLogger.Format("player-0001", BattleTickEvent.None, player);

        Assert.Equal(string.Empty, log);
    }
}
