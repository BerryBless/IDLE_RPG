using GameServer.Entities;
using GameServer.Systems;
using ServerLib.Core.Serialization.Packets;

namespace GameServer.Tests.Systems;

public class SessionBattlePacketsTests
{
    private static Player MakePlayer()
    {
        var player = new Player { InstanceId = "player-abc123", AccountId = 0, Level = 1 };
        player.BaseStats.Hp = 100;
        player.BaseStats.Atk = 10;
        player.UpdateFinalStats();
        player.RestoreResources();
        return player;
    }

    private static Monster MakeMonster(double hp, double maxHp)
    {
        var monster = new Monster
        {
            InstanceId = "m1",
            MonsterId = 2003,
            Level = 3,
            Rewards = new RewardComponent { ExpDrop = 6, GoldDrop = 8 }
        };
        monster.BaseStats.Hp = maxHp;
        monster.BaseStats.Atk = 8;
        monster.UpdateFinalStats();
        monster.RestoreResources();
        monster.FinalStats.CurrentHp = hp; // 테스트에서 임의의 현재 HP를 직접 지정
        return monster;
    }

    [Fact]
    public void BuildTickPackets_None_ReturnsHpOnlyWithUnchangedGeneration()
    {
        var player = MakePlayer();
        var monster = MakeMonster(hp: 40, maxHp: 55);

        var set = SessionBattlePackets.BuildTickPackets(BattleTickEvent.None, player, monster, currentGeneration: 1);

        Assert.Null(set.Death);
        Assert.Equal(40, set.Hp.Hp);
        Assert.Equal(55, set.Hp.MaxHp);
        Assert.Equal(1, set.Hp.Generation);
        Assert.Equal(1, set.NextGeneration);
    }

    [Fact]
    public void BuildTickPackets_PlayerDefeated_ReturnsHpOnlyWithUnchangedGeneration()
    {
        var player = MakePlayer();
        var monster = MakeMonster(hp: 40, maxHp: 55);

        var set = SessionBattlePackets.BuildTickPackets(BattleTickEvent.PlayerDefeated, player, monster, currentGeneration: 3);

        Assert.Null(set.Death);
        Assert.Equal(3, set.Hp.Generation);
        Assert.Equal(3, set.NextGeneration);
    }

    [Fact]
    public void BuildTickPackets_MonsterDefeated_ReturnsDeathThenAdvancesGeneration()
    {
        var player = MakePlayer();
        // Tick()이 이미 monster.RestoreResources()를 호출한 뒤(같은 인스턴스가 풀피로 재등장한 뒤) 이
        // 매퍼가 호출되므로, 여기서도 재등장한 풀피 상태를 그대로 반영한다.
        var monster = MakeMonster(hp: 55, maxHp: 55);

        var set = SessionBattlePackets.BuildTickPackets(BattleTickEvent.MonsterDefeated, player, monster, currentGeneration: 1);

        Assert.NotNull(set.Death);
        Assert.Equal(1, set.Death!.Generation); // 사망한 세대 번호(증가 전)
        Assert.Equal(55, set.Death.TopDamage); // 1인 전투: 몬스터 최대 HP 전량이 유일 기여자의 몫
        Assert.Equal("player-abc123", set.Death.MvpName);

        Assert.Equal(2, set.NextGeneration);
        Assert.Equal(2, set.Hp.Generation); // 재등장한 다음 세대 번호로 HP 패킷 전송
        Assert.Equal(55, set.Hp.Hp);
        Assert.Equal(55, set.Hp.MaxHp);
    }

    [Fact]
    public void BuildTickPackets_ClampsNegativeCurrentHpToZero()
    {
        var player = MakePlayer();
        var monster = MakeMonster(hp: -5, maxHp: 55); // 방어적 케이스: 실제로는 발생하지 않아야 하나 클램프 확인

        var set = SessionBattlePackets.BuildTickPackets(BattleTickEvent.None, player, monster, currentGeneration: 1);

        Assert.Equal(0, set.Hp.Hp);
    }
}
