using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoadTester.Options;

namespace LoadTester.Stress;

/// <summary>스트레스 NDJSON 레코드 스키마. null 필드는 생략.</summary>
internal sealed record StressLine(
    string Ts, string Type,
    // runStart
    string? Scenario = null, int? ProbeClients = null, int? StressClients = null,
    double? BaselineSec = null, double? StressSec = null, double? RecoverySec = null,
    // interval
    string? Phase = null, double? ElapsedSec = null,
    int? ProbeSize = null, int? ProbeConnected = null, int? ProbeAuthenticated = null,
    double? ProbeRttP50Ms = null, double? ProbeRttP95Ms = null,
    long? StressActive = null, long? StressConnectFailures = null, long? StressReconnects = null,
    long? MalformedFramesSent = null, long? StalledHeld = null,
    int? TeleConnected = null, long? TeleRejected = null,
    double? SrvWorkingSetMb = null, double? SrvCpuPct = null, bool? SrvLost = null,
    double? SelfWorkingSetMb = null,
    // runEnd
    string? Verdict = null, IReadOnlyList<string>? Reasons = null, string? Headline = null,
    double? BaselineRttP95Ms = null, int? BaselineServerConnected = null, double? BaselineServerWsMb = null,
    int? PeakServerConnected = null, double? PeakServerWsMb = null,
    bool? SessionCountRecovered = null, double? TimeToRecoverSec = null, double? WsGrowthPerStalledPeerKb = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StressLine))]
internal partial class StressJsonContext : JsonSerializerContext
{
}

/// <summary>스트레스 시계열을 <c>{outRoot}\stress-{scenario}.ndjson</c>으로 기록합니다.</summary>
/// <remarks><b>[Thread Safety:]</b> 단일 라이터(샘플러) 전제. <b>[Blocking:]</b> 레코드당 flush.</remarks>
public sealed class StressNdjsonWriter : IDisposable
{
    private readonly StreamWriter _writer;

    /// <summary>기록 파일 경로.</summary>
    public string FilePath { get; }

    /// <summary>라이터를 생성합니다(디렉터리 없으면 생성, truncate).</summary>
    public StressNdjsonWriter(string outRoot, StressScenarioKind scenario)
    {
        Directory.CreateDirectory(outRoot);
        FilePath = Path.Combine(outRoot, $"stress-{scenario.ToString().ToLowerInvariant()}.ndjson");
        _writer = new StreamWriter(FilePath, append: false);
    }

    private static string Ts() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>runStart 기록.</summary>
    public void WriteRunStart(LoadTestOptions o)
    {
        Write(new StressLine(Ts(), "runStart",
            Scenario: o.Stress?.ToString(), ProbeClients: o.ProbeClients, StressClients: o.StressClients,
            BaselineSec: o.BaselineDuration.TotalSeconds, StressSec: o.StressDuration.TotalSeconds,
            RecoverySec: o.RecoveryDuration.TotalSeconds));
    }

    /// <summary>interval 기록.</summary>
    public void WriteInterval(StressIntervalReport r)
    {
        Write(new StressLine(Ts(), "interval",
            Phase: r.Phase.ToString(), ElapsedSec: r.ElapsedSeconds,
            ProbeSize: r.Probe.Size, ProbeConnected: r.Probe.Connected, ProbeAuthenticated: r.Probe.Authenticated,
            ProbeRttP50Ms: r.Probe.RttP50Ms, ProbeRttP95Ms: r.Probe.RttP95Ms,
            StressActive: r.Driver.StressActive, StressConnectFailures: r.Driver.StressConnectFailures,
            StressReconnects: r.Driver.StressReconnects, MalformedFramesSent: r.Driver.MalformedFramesSent,
            StalledHeld: r.Driver.StalledHeld,
            TeleConnected: r.TeleConnected, TeleRejected: r.TeleRejected,
            SrvWorkingSetMb: r.Resource.ServerWorkingSetMb, SrvCpuPct: r.Resource.ServerCpuPercent,
            SrvLost: r.Resource.ServerProcessLost ? true : null,
            SelfWorkingSetMb: r.Resource.SelfWorkingSetMb));
    }

    /// <summary>runEnd 기록.</summary>
    public void WriteRunEnd(Verdict.Verdict verdict, StressStats s, string headline)
    {
        Write(new StressLine(Ts(), "runEnd",
            Verdict: verdict.Passed ? "PASS" : "FAIL", Reasons: verdict.Reasons, Headline: headline,
            ElapsedSec: s.ElapsedSeconds,
            BaselineRttP95Ms: s.Baseline.ProbeRttP95Ms, BaselineServerConnected: s.Baseline.ServerConnected,
            BaselineServerWsMb: s.Baseline.ServerWsMb,
            PeakServerConnected: s.PeakServerConnected, PeakServerWsMb: s.PeakServerWsMb,
            SessionCountRecovered: s.SessionCountRecovered, TimeToRecoverSec: s.TimeToRecoverSeconds,
            WsGrowthPerStalledPeerKb: s.WsGrowthPerStalledPeerKb));
    }

    private void Write(StressLine line)
    {
        _writer.WriteLine(JsonSerializer.Serialize(line, StressJsonContext.Default.StressLine));
        _writer.Flush();
    }

    /// <inheritdoc/>
    public void Dispose() => _writer.Dispose();
}
