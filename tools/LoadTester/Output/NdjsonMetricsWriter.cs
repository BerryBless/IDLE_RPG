using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoadTester.Metrics;
using LoadTester.Options;

namespace LoadTester.Output;

/// <summary>runStart 레코드 스키마(옵션 에코).</summary>
internal sealed record RunStartLine(
    string Ts, string Type, string Mode, int Clients, int RampUp, double DurationSec,
    string Host, int GamePort, int AuthPort, int TelemetryPort, int Accounts);

/// <summary>interval 레코드 스키마. null 필드는 직렬화에서 생략된다.</summary>
internal sealed record IntervalLine(
    string Ts, string Type, double ElapsedSec,
    int Active, int Target, int Authenticated,
    long ConnectAttempts, long ConnectFail, long AuthOk, long AuthFail, long AuthTimeout, long LoginFail,
    long UnexpectedDisconnects, long Reconnects,
    long Broadcasts, long BytesIn, int StalledClients, bool StallIncident,
    double RttP50Ms, double RttP95Ms, double RttP99Ms,
    double CumRttP50Ms, double CumRttP95Ms, double CumRttP99Ms,
    int? TeleConnected, long? TeleRejected, int? TeleGeneration, double? TeleBossHpPct,
    double? SrvWorkingSetMb, double? SrvCpuPct,
    double SelfWorkingSetMb, int SelfThreads, int SelfGen2);

/// <summary>runEnd 레코드 스키마.</summary>
internal sealed record RunEndLine(
    string Ts, string Type, string Verdict, IReadOnlyList<string> Reasons, double ElapsedSec,
    double CumRttP50Ms, double CumRttP95Ms, double CumRttP99Ms,
    long ConnectAttempts, long ConnectFail, long AuthOk, long AuthFail, long AuthTimeout, long LoginFail,
    long UnexpectedDisconnects, long Reconnects, long Broadcasts, long BytesIn,
    int StallIncidents, double RetentionMean, double? MaxServerWorkingSetMb, bool AllClientsAuthenticated);

// STJ 소스 생성 컨텍스트: 런타임 리플렉션 직렬화 대신 컴파일 시점 생성 코드를 사용해
// 첫 직렬화 지연·리플렉션 메타데이터 할당을 제거한다(GameEventJsonContext와 동일 근거).
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RunStartLine))]
[JsonSerializable(typeof(IntervalLine))]
[JsonSerializable(typeof(RunEndLine))]
internal partial class LoadTestJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 부하 테스트 시계열을 NDJSON 파일(<c>loadtest-{yyyyMMdd-HHmmss}.ndjson</c>)로 기록합니다.
/// 파일이 크기 상한을 넘거나 정시(hour) 경계를 지나면 새 파일로 로테이션해 72시간 실행에서도
/// 단일 파일이 무한 성장하지 않는다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not thread-safe — 단일 라이터 전제(샘플러 스레드,
/// 시작/종료 레코드는 샘플러 기동 전/정지 후 메인 스레드). GameEventSink처럼 채널을 두지 않은
/// 이유: 생산자가 하나뿐이라 MPSC 큐가 불필요하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 레코드당 JSON 문자열 1개(10초 주기 — 무해).</description></item>
/// <item><description><b>Blocking:</b> 레코드당 동기 파일 쓰기+flush(수 µs~ms). 샘플러 주기
/// 대비 무시 가능하며, flush 즉시성으로 실행 중 tail 관찰을 보장한다.</description></item>
/// </list>
/// </remarks>
public sealed class NdjsonMetricsWriter : IDisposable
{
    private readonly string _directory;
    private readonly long _maxBytes;

    private StreamWriter? _writer;
    private FileStream? _stream;
    private int _openedHour = -1;

    /// <summary>현재 기록 중인 파일 경로(테스트·안내 출력용). 아직 안 열렸으면 null.</summary>
    public string? CurrentFilePath { get; private set; }

    /// <summary>라이터를 생성합니다. 파일은 첫 기록 시점에 연다.</summary>
    /// <param name="directory">출력 디렉터리(없으면 생성).</param>
    /// <param name="maxLogMb">파일 크기 로테이션 상한(MB).</param>
    public NdjsonMetricsWriter(string directory, int maxLogMb)
    {
        _directory = directory;
        _maxBytes = (long)maxLogMb * 1024 * 1024;
    }

    private static string Ts(DateTime utc) =>
        // InvariantCulture: 비그레고리력 문화권에서 ts가 깨지는 것을 방지(GameEventSink.Ts와 동일 근거).
        utc.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>runStart 레코드를 기록합니다.</summary>
    public void WriteRunStart(LoadTestOptions options)
    {
        var line = new RunStartLine(
            Ts(DateTime.UtcNow), "runStart", options.Mode, options.Clients, options.RampUpPerSecond,
            options.Duration.TotalSeconds, options.Host, options.GamePort, options.AuthPort,
            options.TelemetryPort, options.Accounts);
        WriteLine(JsonSerializer.Serialize(line, LoadTestJsonContext.Default.RunStartLine));
    }

    /// <summary>interval 레코드를 기록합니다.</summary>
    public void WriteInterval(IntervalReport r)
    {
        var line = new IntervalLine(
            Ts(DateTime.UtcNow), "interval", r.ElapsedSeconds,
            r.Active, r.Target, r.Authenticated,
            r.Totals.ConnectAttempts, r.Totals.ConnectFailures, r.Totals.AuthSuccesses,
            r.Totals.AuthFailures, r.Totals.AuthTimeouts, r.Totals.LoginFailures,
            r.Totals.UnexpectedDisconnects, r.Totals.Reconnects,
            r.BroadcastsDelta, r.BytesInDelta, r.StalledClients, r.FullStall,
            r.RttP50Ms, r.RttP95Ms, r.RttP99Ms,
            r.CumRttP50Ms, r.CumRttP95Ms, r.CumRttP99Ms,
            r.TeleConnected, r.TeleRejected, r.TeleGeneration, r.TeleBossHpPct,
            r.Resource.ServerWorkingSetMb, r.Resource.ServerCpuPercent,
            r.Resource.SelfWorkingSetMb, r.Resource.SelfThreadCount, r.Resource.SelfGen2Collections);
        WriteLine(JsonSerializer.Serialize(line, LoadTestJsonContext.Default.IntervalLine));
    }

    /// <summary>runEnd 레코드를 기록합니다.</summary>
    public void WriteRunEnd(LoadTester.Verdict.Verdict verdict, LoadTester.Verdict.FinalStats stats)
    {
        var line = new RunEndLine(
            Ts(DateTime.UtcNow), "runEnd", verdict.Passed ? "PASS" : "FAIL", verdict.Reasons,
            stats.ElapsedSeconds,
            stats.CumRttP50Ms, stats.CumRttP95Ms, stats.CumRttP99Ms,
            stats.Totals.ConnectAttempts, stats.Totals.ConnectFailures, stats.Totals.AuthSuccesses,
            stats.Totals.AuthFailures, stats.Totals.AuthTimeouts, stats.Totals.LoginFailures,
            stats.Totals.UnexpectedDisconnects, stats.Totals.Reconnects,
            stats.Totals.Broadcasts, stats.Totals.BytesIn,
            stats.StallIncidents, stats.RetentionMean, stats.MaxServerWorkingSetMb,
            stats.AllClientsEverAuthenticated);
        WriteLine(JsonSerializer.Serialize(line, LoadTestJsonContext.Default.RunEndLine));
    }

    private void WriteLine(string json)
    {
        RotateIfNeeded();
        _writer!.WriteLine(json);
        // 레코드당 flush: 72시간 실행 중 언제 강제 종료돼도 마지막 구간까지 파일에 남는다.
        _writer.Flush();
    }

    private void RotateIfNeeded()
    {
        DateTime now = DateTime.UtcNow;
        bool needOpen = _writer is null;
        bool hourCrossed = _openedHour >= 0 && now.Hour != _openedHour;
        bool tooLarge = _stream is not null && _stream.Length >= _maxBytes;

        if (!needOpen && !hourCrossed && !tooLarge)
            return;

        _writer?.Dispose(); // StreamWriter.Dispose가 내부 FileStream까지 닫는다
        Directory.CreateDirectory(_directory);

        string name = $"loadtest-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.ndjson";
        string path = Path.Combine(_directory, name);
        // 같은 초에 로테이션이 겹치면(테스트의 극단 케이스) 덮어쓰기 대신 접미사를 붙인다.
        int suffix = 1;
        while (File.Exists(path))
            path = Path.Combine(_directory, $"loadtest-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{suffix++}.ndjson");

        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(_stream);
        _openedHour = now.Hour;
        CurrentFilePath = path;
    }

    /// <summary>파일을 flush하고 닫습니다.</summary>
    public void Dispose()
    {
        _writer?.Dispose();
        _writer = null;
        _stream = null;
    }
}
