using GameServer.Systems;
using ServerLib.Core.Serialization.Packets;

namespace GameServer.Tests.Systems;

/// <summary>
/// <see cref="RaidBroadcastPackets.Build"/>(소켓 없는 순수 매퍼)가 <see cref="RaidStepBroadcast"/>를
/// <see cref="MobHpPacket"/>/<see cref="MobDeathPacket"/>으로 올바르게 변환하는지 검증한다. 사이클 1의
/// <c>SessionBattlePacketsTests</c>와 동형 — 소켓·RaidEncounter 없이 순수 값만으로 결정적 테스트.
/// </summary>
public class RaidBroadcastPacketsTests
{
    [Fact]
    public void Build_None_ReturnsHpOnlyAtNewGeneration()
    {
        var info = new RaidStepBroadcast(RaidEventType.None, CurrentHp: 60, MaxHp: 100,
            DeadGeneration: 1, NewGeneration: 1, MvpName: string.Empty, TopDamage: 0);

        var (death, hp) = RaidBroadcastPackets.Build(info);

        Assert.Null(death);
        Assert.Equal(60, hp.Hp);
        Assert.Equal(100, hp.MaxHp);
        Assert.Equal(1, hp.Generation);
    }

    [Fact]
    public void Build_BossDamaged_ReturnsHpOnlyAtNewGeneration()
    {
        var info = new RaidStepBroadcast(RaidEventType.BossDamaged, CurrentHp: 42, MaxHp: 100,
            DeadGeneration: 3, NewGeneration: 3, MvpName: string.Empty, TopDamage: 0);

        var (death, hp) = RaidBroadcastPackets.Build(info);

        Assert.Null(death);
        Assert.Equal(42, hp.Hp);
        Assert.Equal(100, hp.MaxHp);
        Assert.Equal(3, hp.Generation);
    }

    [Fact]
    public void Build_BossDefeated_ReturnsDeathAtDeadGenerationAndFullHpAtNewGeneration()
    {
        var info = new RaidStepBroadcast(RaidEventType.BossDefeated, CurrentHp: 100, MaxHp: 100,
            DeadGeneration: 1, NewGeneration: 2, MvpName: "player-p2", TopDamage: 70);

        var (death, hp) = RaidBroadcastPackets.Build(info);

        Assert.NotNull(death);
        Assert.Equal(1, death!.Generation);
        Assert.Equal(70, death.TopDamage);
        Assert.Equal("player-p2", death.MvpName);

        Assert.Equal(100, hp.Hp);
        Assert.Equal(100, hp.MaxHp);
        Assert.Equal(2, hp.Generation); // 재등장 — 새 세대
    }

    [Fact]
    public void Build_RaidFailed_ReturnsHpOnlyAtNewGeneration_NoDeathPacket()
    {
        // 실패는 보상도 MVP도 없다 — 처치가 아니므로 MobDeathPacket을 만들지 않는다(설계 결정).
        var info = new RaidStepBroadcast(RaidEventType.RaidFailed, CurrentHp: 5_000_000, MaxHp: 5_000_000,
            DeadGeneration: 1, NewGeneration: 2, MvpName: string.Empty, TopDamage: 0);

        var (death, hp) = RaidBroadcastPackets.Build(info);

        Assert.Null(death);
        Assert.Equal(5_000_000, hp.Hp);
        Assert.Equal(5_000_000, hp.MaxHp);
        Assert.Equal(2, hp.Generation);
    }

    [Fact]
    public void Build_ClampsNegativeCurrentHpToZero()
    {
        // RaidStepBroadcast 자체가 RaidEncounter 쪽에서 이미 클램프하지만(Math.Max(0, ...)), 매퍼도
        // 방어적으로 한 번 더 클램프해 계약을 명시적으로 지킨다(SessionBattlePackets와 동일 관례).
        var info = new RaidStepBroadcast(RaidEventType.BossDamaged, CurrentHp: -5, MaxHp: 100,
            DeadGeneration: 1, NewGeneration: 1, MvpName: string.Empty, TopDamage: 0);

        var (_, hp) = RaidBroadcastPackets.Build(info);

        Assert.Equal(0, hp.Hp);
    }
}
