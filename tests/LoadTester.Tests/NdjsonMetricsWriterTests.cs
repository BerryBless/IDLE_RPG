using System.Text.Json;
using LoadTester.Metrics;
using LoadTester.Options;
using LoadTester.Output;
using LoadTester.Verdict;

namespace LoadTester.Tests;

/// <summary><see cref="NdjsonMetricsWriter"/>의 레코드 스키마·크기 로테이션 검증(임시 디렉터리 사용).</summary>
public class NdjsonMetricsWriterTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"loadtester-ndjson-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static IntervalReport SampleReport() => new(
        ElapsedSeconds: 10, Active: 5, Target: 10, Authenticated: 5,
        Totals: new CounterTotals(10, 1, 5, 0, 0, 0, 2, 3, 1000, 20000),
        BroadcastsDelta: 500, BytesInDelta: 10000,
        StalledClients: 0, FullStall: false,
        RttP50Ms: 3.1, RttP95Ms: 11, RttP99Ms: 38,
        CumRttP50Ms: 3.0, CumRttP95Ms: 10, CumRttP99Ms: 35,
        TeleConnected: 5, TeleRejected: 0, TeleGeneration: 4, TeleBossHpPct: 63.2,
        Resource: new ResourceSample(812.4, 34.1, false, 120.5, 42, 7));

    [Fact]
    public void 세종류레코드_유효한_NDJSON으로_기록된다()
    {
        using (var writer = new NdjsonMetricsWriter(_tempDir, maxLogMb: 256))
        {
            LoadTestOptions.TryParse([], out var options, out _);
            writer.WriteRunStart(options!);
            writer.WriteInterval(SampleReport());
            var stats = new FinalStats(3, 10, 35,
                new CounterTotals(10, 1, 5, 0, 0, 0, 2, 3, 1000, 20000),
                AllClientsEverAuthenticated: true, StallIncidents: 0, RetentionMean: 0.995,
                MaxServerWorkingSetMb: 812.4, ServerProcessLostObserved: false, ElapsedSeconds: 60);
            writer.WriteRunEnd(new Verdict.Verdict(true, []), stats);
        }

        string[] files = Directory.GetFiles(_tempDir, "loadtest-*.ndjson");
        Assert.Single(files);

        string[] lines = File.ReadAllLines(files[0]);
        Assert.Equal(3, lines.Length);

        using var runStart = JsonDocument.Parse(lines[0]);
        Assert.Equal("runStart", runStart.RootElement.GetProperty("type").GetString());
        Assert.Equal("game", runStart.RootElement.GetProperty("mode").GetString());

        using var interval = JsonDocument.Parse(lines[1]);
        Assert.Equal("interval", interval.RootElement.GetProperty("type").GetString());
        Assert.Equal(5, interval.RootElement.GetProperty("active").GetInt32());
        Assert.Equal(3.1, interval.RootElement.GetProperty("rttP50Ms").GetDouble());
        Assert.Equal(812.4, interval.RootElement.GetProperty("srvWorkingSetMb").GetDouble());

        using var runEnd = JsonDocument.Parse(lines[2]);
        Assert.Equal("runEnd", runEnd.RootElement.GetProperty("type").GetString());
        Assert.Equal("PASS", runEnd.RootElement.GetProperty("verdict").GetString());
    }

    [Fact]
    public void 텔레메트리없는구간_null필드는_생략된다()
    {
        using (var writer = new NdjsonMetricsWriter(_tempDir, maxLogMb: 256))
        {
            var report = SampleReport() with
            {
                TeleConnected = null, TeleRejected = null, TeleGeneration = null, TeleBossHpPct = null,
                Resource = new ResourceSample(null, null, false, 120.5, 42, 7),
            };
            writer.WriteInterval(report);
        }

        string line = File.ReadAllLines(Directory.GetFiles(_tempDir, "*.ndjson")[0])[0];
        using var doc = JsonDocument.Parse(line);
        Assert.False(doc.RootElement.TryGetProperty("teleConnected", out _));
        Assert.False(doc.RootElement.TryGetProperty("srvWorkingSetMb", out _));
        Assert.True(doc.RootElement.TryGetProperty("selfWorkingSetMb", out _));
    }

    [Fact]
    public void 크기상한초과시_새파일로_로테이션된다()
    {
        // maxLogMb=1: 1MB를 넘기려면 interval 레코드(~600B) 약 1800건 — 2500건 기록해 로테이션 강제.
        using (var writer = new NdjsonMetricsWriter(_tempDir, maxLogMb: 1))
        {
            var report = SampleReport();
            for (int i = 0; i < 2500; i++)
                writer.WriteInterval(report);
        }

        string[] files = Directory.GetFiles(_tempDir, "loadtest-*.ndjson");
        Assert.True(files.Length >= 2, $"로테이션으로 파일이 2개 이상이어야 하는데 {files.Length}개");

        // 모든 파일의 총 라인 수는 기록 건수와 일치(유실 없음).
        int totalLines = files.Sum(f => File.ReadAllLines(f).Length);
        Assert.Equal(2500, totalLines);
    }
}
