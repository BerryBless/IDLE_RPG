using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class GameEventSinkTests
{
    [Fact]
    public void MonsterDefeatedLine_ShapesJson()
    {
        var ts = new DateTime(2026, 7, 7, 15, 20, 1, DateTimeKind.Utc);

        var line = GameEventSink.MonsterDefeatedLine(ts, "player-0042", 3, 41, 22);

        Assert.Equal(
            """{"ts":"2026-07-07T15:20:01Z","type":"MonsterDefeated","playerId":"player-0042","level":3,"exp":41,"gold":22}""",
            line);
    }

    [Fact]
    public void PlayerDefeatedLine_OmitsUnusedFields()
    {
        var ts = new DateTime(2026, 7, 7, 15, 20, 2, DateTimeKind.Utc);

        var line = GameEventSink.PlayerDefeatedLine(ts, "player-0187");

        Assert.Equal("""{"ts":"2026-07-07T15:20:02Z","type":"PlayerDefeated","playerId":"player-0187"}""", line);
    }

    [Fact]
    public void RaidBossDefeatedLine_OmitsNullFields()
    {
        var ts = new DateTime(2026, 7, 7, 15, 20, 3, DateTimeKind.Utc);

        var line = GameEventSink.RaidBossDefeatedLine(ts, 100);

        Assert.Equal("""{"ts":"2026-07-07T15:20:03Z","type":"RaidBossDefeated","contributors":100}""", line);
    }

    [Fact]
    public void RaidFailedLine_HasOnlyTsAndType()
    {
        var ts = new DateTime(2026, 7, 7, 15, 20, 4, DateTimeKind.Utc);

        var line = GameEventSink.RaidFailedLine(ts);

        Assert.Equal("""{"ts":"2026-07-07T15:20:04Z","type":"RaidFailed"}""", line);
    }

    [Fact]
    public void TickExceptionLine_IncludesErrorMessage()
    {
        var ts = new DateTime(2026, 7, 7, 15, 20, 5, DateTimeKind.Utc);

        var line = GameEventSink.TickExceptionLine(ts, "player-0007", "Object reference not set");

        Assert.Equal(
            """{"ts":"2026-07-07T15:20:05Z","type":"TickException","playerId":"player-0007","error":"Object reference not set"}""",
            line);
    }

    [Fact]
    public void RecordMonsterDefeated_IncrementsMetricAndWritesLine()
    {
        // 병렬 실행 중인 다른 테스트와 Meter 이름이 겹치지 않도록 고유 이름을 쓴다(GameMetricsTests와 동일 이유).
        var metrics = new GameMetrics($"Test.GameEventSink.{Guid.NewGuid()}");
        long sum = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "game.monster.defeated")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => sum += measurement);
        listener.Start();

        using var stringWriter = new StringWriter();
        var sink = new GameEventSink(stringWriter, metrics);

        sink.RecordMonsterDefeated("player-0001", 1, 6, 8);

        Assert.Equal(1, sum);
    }
}
