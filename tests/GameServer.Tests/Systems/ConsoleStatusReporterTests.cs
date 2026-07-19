using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class ConsoleStatusReporterTests
{
    [Fact]
    public void Diff_ComputesPerCounterDeltaAndPassesThroughLatestBossState()
    {
        var previous = new GameEventCounts(
            PlayerConnected: 5, PlayerDisconnected: 3, PlayerConnectionErrors: 1,
            RaidBossDefeated: 2, RaidFailed: 1, MonsterDefeated: 10, PlayerDefeated: 4, TickExceptions: 0,
            LastBossHpPercent: 90.0, LastBossGeneration: 2);
        var current = new GameEventCounts(
            PlayerConnected: 8, PlayerDisconnected: 3, PlayerConnectionErrors: 1,
            RaidBossDefeated: 3, RaidFailed: 1, MonsterDefeated: 15, PlayerDefeated: 4, TickExceptions: 1,
            LastBossHpPercent: 42.5, LastBossGeneration: 3);

        var delta = ConsoleStatusReporter.Diff(previous, current);

        Assert.Equal(3, delta.PlayerConnected);
        Assert.Equal(0, delta.PlayerDisconnected);
        Assert.Equal(0, delta.PlayerConnectionErrors);
        Assert.Equal(1, delta.RaidBossDefeated);
        Assert.Equal(0, delta.RaidFailed);
        Assert.Equal(5, delta.MonsterDefeated);
        Assert.Equal(0, delta.PlayerDefeated);
        Assert.Equal(1, delta.TickExceptions);
        // LastBossHpPercent/LastBossGeneration은 델타 개념이 없는 "최신값" 필드라 current를 그대로 통과시킨다.
        Assert.Equal(42.5, delta.LastBossHpPercent);
        Assert.Equal(3, delta.LastBossGeneration);
    }

    [Fact]
    public void HasActivity_AllZeroDelta_ReturnsFalse()
    {
        var delta = new GameEventCounts(0, 0, 0, 0, 0, 0, 0, 0, 50.0, 1);

        Assert.False(ConsoleStatusReporter.HasActivity(delta));
    }

    [Theory]
    [InlineData(1, 0, 0, 0, 0, 0, 0, 0)]
    [InlineData(0, 1, 0, 0, 0, 0, 0, 0)]
    [InlineData(0, 0, 1, 0, 0, 0, 0, 0)]
    [InlineData(0, 0, 0, 1, 0, 0, 0, 0)]
    [InlineData(0, 0, 0, 0, 1, 0, 0, 0)]
    [InlineData(0, 0, 0, 0, 0, 1, 0, 0)]
    [InlineData(0, 0, 0, 0, 0, 0, 1, 0)]
    [InlineData(0, 0, 0, 0, 0, 0, 0, 1)]
    public void HasActivity_AnySingleNonZeroCounter_ReturnsTrue(
        long connected, long disconnected, long connErrors, long bossDefeated,
        long raidFailed, long monsterDefeated, long playerDefeated, long tickExceptions)
    {
        var delta = new GameEventCounts(
            connected, disconnected, connErrors, bossDefeated, raidFailed, monsterDefeated, playerDefeated,
            tickExceptions, LastBossHpPercent: 0, LastBossGeneration: 0);

        Assert.True(ConsoleStatusReporter.HasActivity(delta));
    }

    [Fact]
    public void FormatLine_ActiveWindow_IncludesDeltaCountersAndWindowSeconds()
    {
        var now = new DateTime(2026, 7, 20, 23, 47, 5, DateTimeKind.Utc);
        var delta = new GameEventCounts(
            PlayerConnected: 2, PlayerDisconnected: 0, PlayerConnectionErrors: 0,
            RaidBossDefeated: 1, RaidFailed: 0, MonsterDefeated: 0, PlayerDefeated: 0, TickExceptions: 0,
            LastBossHpPercent: 68.2, LastBossGeneration: 4);

        var line = ConsoleStatusReporter.FormatLine(
            now, connectedCount: 3, rejectedConnections: 0, delta,
            bossHpPercent: 68.2, generation: 4, idle: false, window: TimeSpan.FromSeconds(5));

        Assert.Equal(
            "[23:47:05] players=3 boss=68.2%(gen 4) | +conn 2 -disc 0 kills 1 fails 0 err 0 (last 5s)",
            line);
    }

    [Fact]
    public void FormatLine_Idle_OmitsDeltaCountersAndShowsIdleMarker()
    {
        var now = new DateTime(2026, 7, 20, 23, 48, 0, DateTimeKind.Utc);
        var delta = new GameEventCounts(0, 0, 0, 0, 0, 0, 0, 0, 100.0, 4);

        var line = ConsoleStatusReporter.FormatLine(
            now, connectedCount: 0, rejectedConnections: 0, delta,
            bossHpPercent: 100.0, generation: 4, idle: true, window: TimeSpan.FromSeconds(5));

        Assert.Equal("[23:48:00] players=0 boss=100.0%(gen 4) idle", line);
    }

    [Fact]
    public void FormatLine_ActiveWindow_WithRejectedConnections_AppendsRejectedSuffix()
    {
        var now = new DateTime(2026, 7, 20, 23, 50, 0, DateTimeKind.Utc);
        var delta = new GameEventCounts(0, 0, 0, 0, 0, 0, 0, 0, 55.0, 1);

        var line = ConsoleStatusReporter.FormatLine(
            now, connectedCount: 1, rejectedConnections: 7, delta,
            bossHpPercent: 55.0, generation: 1, idle: false, window: TimeSpan.FromSeconds(5));

        Assert.Equal(
            "[23:50:00] players=1 boss=55.0%(gen 1) | +conn 0 -disc 0 kills 0 fails 0 err 0 rejected 7 (last 5s)",
            line);
    }
}
