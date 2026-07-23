using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoadTester.Verdict;

namespace LoadTester.Coordination;

/// <summary>코디네이터 통합 NDJSON 레코드 스키마(runStart/interval/runEnd). null 필드는 생략.</summary>
internal sealed record CombinedLine(
    string Ts, string Type,
    // runStart
    int? Workers = null, int? PortCount = null, int? TargetConcurrent = null, double? DurationSec = null,
    // interval
    double? ElapsedSec = null, int? WorkersReporting = null, int? Active = null, int? Target = null,
    int? Authenticated = null, long? ConnectAttempts = null, long? TotalFailures = null,
    long? UnexpectedDisconnects = null, long? Reconnects = null, int? StalledClients = null,
    double? MaxWorkerWorkingSetMb = null, int? TeleConnected = null, long? TeleRejected = null,
    double? SrvWorkingSetMb = null, double? SrvCpuPct = null,
    // runEnd
    string? Verdict = null, IReadOnlyList<string>? Reasons = null, int? PeakAuthenticated = null,
    int? PeakTeleConnected = null, double? RetentionMeanAfterRamp = null, bool? RampCompleted = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CombinedLine))]
internal partial class CombinedJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 코디네이터 통합 시계열을 <c>{outRoot}\combined.ndjson</c>으로 기록합니다. 워커별 NDJSON은 각
/// 워커가 <c>worker-{i}</c>에 병행 기록하므로, 이 파일은 전 워커 합산 + 서버측 관측만 담습니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Not thread-safe — 코디네이터 집계 루프 단일 라이터 전제.
/// <b>[Blocking:]</b> 레코드당 동기 flush(주기당 1회 — 무해). <b>[Memory:]</b> 레코드당 문자열 1개.
/// </remarks>
public sealed class CombinedNdjsonWriter : IDisposable
{
    private readonly StreamWriter _writer;

    /// <summary>기록 파일 경로.</summary>
    public string FilePath { get; }

    /// <summary>통합 NDJSON 라이터를 생성합니다(출력 디렉터리 없으면 생성, truncate 오픈).</summary>
    public CombinedNdjsonWriter(string outRoot)
    {
        Directory.CreateDirectory(outRoot);
        FilePath = Path.Combine(outRoot, "combined.ndjson");
        _writer = new StreamWriter(FilePath, append: false);
    }

    private static string Ts() =>
        DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>runStart 레코드를 기록합니다.</summary>
    public void WriteRunStart(int workers, int portCount, int targetConcurrent, double durationSec)
    {
        Write(new CombinedLine(Ts(), "runStart", Workers: workers, PortCount: portCount,
            TargetConcurrent: targetConcurrent, DurationSec: durationSec));
    }

    /// <summary>interval 레코드를 기록합니다.</summary>
    public void WriteInterval(CombinedInterval c)
    {
        Write(new CombinedLine(Ts(), "interval",
            ElapsedSec: c.ElapsedSeconds, WorkersReporting: c.WorkersReporting, Active: c.Active,
            Target: c.Target, Authenticated: c.Authenticated, ConnectAttempts: c.ConnectAttempts,
            TotalFailures: c.TotalFailures, UnexpectedDisconnects: c.UnexpectedDisconnects,
            Reconnects: c.Reconnects, StalledClients: c.StalledClients,
            MaxWorkerWorkingSetMb: c.MaxWorkerWorkingSetMb, TeleConnected: c.TeleConnected,
            TeleRejected: c.TeleRejected, SrvWorkingSetMb: c.ServerWorkingSetMb, SrvCpuPct: c.ServerCpuPercent));
    }

    /// <summary>runEnd 레코드를 기록합니다.</summary>
    public void WriteRunEnd(Verdict.Verdict verdict, CapacityStats stats)
    {
        Write(new CombinedLine(Ts(), "runEnd",
            Verdict: verdict.Passed ? "PASS" : "FAIL", Reasons: verdict.Reasons,
            PeakAuthenticated: stats.PeakAuthenticated, PeakTeleConnected: stats.PeakTeleConnected,
            RetentionMeanAfterRamp: stats.RetentionMeanAfterRamp, RampCompleted: stats.RampCompleted,
            ElapsedSec: stats.ElapsedSeconds));
    }

    private void Write(CombinedLine line)
    {
        _writer.WriteLine(JsonSerializer.Serialize(line, CombinedJsonContext.Default.CombinedLine));
        _writer.Flush();
    }

    /// <inheritdoc/>
    public void Dispose() => _writer.Dispose();
}
