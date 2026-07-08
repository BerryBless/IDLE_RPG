using System.Diagnostics.Metrics;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class GameMetricsTests
{
    [Fact]
    public void AllInstruments_RecordExpectedValues()
    {
        // 병렬 실행 중인 다른 테스트의 GameMetrics와 Meter 이름이 겹치지 않도록 테스트마다 고유
        // 이름을 준다 — 안 그러면 MeterListener가 다른 테스트의 측정값까지 주워 카운트가 어긋난다.
        var meterName = $"Test.GameMetrics.{Guid.NewGuid()}";
        using var metrics = new GameMetrics(meterName);

        var counters = new Dictionary<string, long>();
        double gaugeValue = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == meterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            counters[instrument.Name] = counters.GetValueOrDefault(instrument.Name) + measurement);
        listener.SetMeasurementEventCallback<double>((_, measurement, _, _) => gaugeValue = measurement);
        listener.Start();

        metrics.MonsterDefeated();
        metrics.PlayerDefeated();
        metrics.RaidBossDefeated();
        metrics.RaidFailed();
        metrics.TickException();
        metrics.RaidBossHpPercent(42.5);
        metrics.PlayerConnected();
        metrics.PlayerDisconnected();
        metrics.PlayerConnectionError();

        Assert.Equal(1, counters["game.monster.defeated"]);
        Assert.Equal(1, counters["game.player.defeated"]);
        Assert.Equal(1, counters["game.raid.boss_defeated"]);
        Assert.Equal(1, counters["game.raid.failed"]);
        Assert.Equal(1, counters["game.tick.exceptions"]);
        Assert.Equal(42.5, gaugeValue);
        Assert.Equal(1, counters["game.player.connected"]);
        Assert.Equal(1, counters["game.player.disconnected"]);
        Assert.Equal(1, counters["game.player.connection_errors"]);
    }
}
