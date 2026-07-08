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
    public void PlayerConnectedLine_IncludesPlayerIdAndLevel()
    {
        var ts = new DateTime(2026, 7, 8, 10, 0, 0, DateTimeKind.Utc);

        var line = GameEventSink.PlayerConnectedLine(ts, "player-abc123", 1);

        Assert.Equal(
            """{"ts":"2026-07-08T10:00:00Z","type":"PlayerConnected","playerId":"player-abc123","level":1}""",
            line);
    }

    [Fact]
    public void PlayerDisconnectedLine_HasOnlyTsTypeAndPlayerId()
    {
        var ts = new DateTime(2026, 7, 8, 10, 0, 1, DateTimeKind.Utc);

        var line = GameEventSink.PlayerDisconnectedLine(ts, "player-abc123");

        Assert.Equal("""{"ts":"2026-07-08T10:00:01Z","type":"PlayerDisconnected","playerId":"player-abc123"}""", line);
    }

    [Fact]
    public void PlayerConnectionErrorLine_IncludesErrorMessage()
    {
        var ts = new DateTime(2026, 7, 8, 10, 0, 2, DateTimeKind.Utc);

        var line = GameEventSink.PlayerConnectionErrorLine(ts, "player-abc123", "Connection reset");

        Assert.Equal(
            """{"ts":"2026-07-08T10:00:02Z","type":"PlayerConnectionError","playerId":"player-abc123","error":"Connection reset"}""",
            line);
    }

    [Fact]
    public void RecordPlayerConnected_IncrementsMetricAndWritesLine()
    {
        var meterName = $"Test.GameEventSink.{Guid.NewGuid()}";
        var metrics = new GameMetrics(meterName);
        long sum = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "game.player.connected" && instrument.Meter.Name == meterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => sum += measurement);
        listener.Start();

        using var stringWriter = new StringWriter();
        var sink = new GameEventSink(stringWriter, metrics);

        sink.RecordPlayerConnected("player-0001", 1);

        Assert.Equal(1, sum);
    }

    [Fact]
    public void RecordMonsterDefeated_IncrementsMetricAndWritesLine()
    {
        // 병렬 실행 중인 다른 테스트와 Meter 이름이 겹치지 않도록 고유 이름을 쓴다(GameMetricsTests와 동일 이유).
        var meterName = $"Test.GameEventSink.{Guid.NewGuid()}";
        var metrics = new GameMetrics(meterName);
        long sum = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            // instrument.Name만 보면 다른 테스트 클래스가 병렬로 만든 동명 계측기(game.monster.defeated)의
            // 측정값까지 주워버려 간헐적으로 sum이 1을 초과한다 — Meter.Name까지 함께 걸러야 이 테스트가
            // 만든 GameMetrics 인스턴스만 관측한다(GameMetricsTests와 동일 패턴).
            if (instrument.Name == "game.monster.defeated" && instrument.Meter.Name == meterName)
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
