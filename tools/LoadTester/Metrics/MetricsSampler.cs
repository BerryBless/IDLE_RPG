using System.Diagnostics;
using LoadTester.Client;
using LoadTester.Options;
using LoadTester.Output;
using LoadTester.Telemetry;
using LoadTester.Verdict;

namespace LoadTester.Metrics;

/// <summary>
/// 유일한 주기 관측 루프입니다. 리포트 주기마다 전 클라이언트 스냅샷을 순회해
/// RTT 히스토그램 기록·스톨 계산을 수행하고, 카운터 델타·텔레메트리·리소스 샘플을 합쳐
/// <see cref="IntervalReport"/>를 만들어 콘솔·NDJSON·판정기에 전달합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <see cref="RunAsync"/> 태스크 1개 전용. 히스토그램
/// (단일 라이터)·판정기·NDJSON 라이터의 유일한 접근자가 이 루프라는 것이 전체 설계의
/// 동기화 생략 근거다.</description></item>
/// <item><description><b>Memory Allocation:</b> 구간당 IntervalReport 1개 + 구간 히스토그램 사본
/// 1개(10초 주기 — 무해). 클라이언트 순회는 무할당(스냅샷은 record struct).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. PeriodicTimer 비동기 대기.</description></item>
/// </list>
/// </remarks>
public sealed class MetricsSampler
{
    private readonly LoadTestOptions _options;
    private readonly IReadOnlyList<VirtualClient> _clients;
    private readonly MetricsAggregator _metrics;
    private readonly TelemetrySubscriber? _telemetry;
    private readonly ResourceMonitor _resources;
    private readonly IIntervalReporter _console;
    private readonly NdjsonMetricsWriter _writer;
    private readonly VerdictEvaluator _verdict;

    // 누적/구간 히스토그램 2개: 구간본은 리포트마다 CopyAndReset으로 비우고,
    // 누적본은 실행 전체의 판정용 분포를 유지한다. 둘 다 이 샘플러만 쓴다(단일 라이터).
    private readonly RttHistogram _cumulative = new();
    private readonly RttHistogram _interval = new();

    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private CounterTotals _previousTotals;
    private int _peakAuthenticated;

    /// <summary>실행 중 관측된 최대 동시 인증 클라이언트 수(워커 최종 보고·상한 탐침용).</summary>
    public int PeakAuthenticated => _peakAuthenticated;

    /// <summary>샘플러를 생성합니다.</summary>
    public MetricsSampler(LoadTestOptions options, IReadOnlyList<VirtualClient> clients,
        MetricsAggregator metrics, TelemetrySubscriber? telemetry, ResourceMonitor resources,
        IIntervalReporter console, NdjsonMetricsWriter writer, VerdictEvaluator verdict)
    {
        _options = options;
        _clients = clients;
        _metrics = metrics;
        _telemetry = telemetry;
        _resources = resources;
        _console = console;
        _writer = writer;
        _verdict = verdict;
    }

    /// <summary>취소될 때까지 리포트 주기로 관측을 반복합니다.</summary>
    public async Task RunAsync(CancellationToken lifetime)
    {
        // PeriodicTimer: Task.Delay 루프와 달리 tick마다 타이머 객체를 재생성하지 않고,
        // 처리 시간이 길어져도 다음 tick이 밀리기만 할 뿐 겹쳐 실행되지 않는다.
        using var timer = new PeriodicTimer(_options.ReportInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime))
            {
                IntervalReport report = BuildInterval();
                _console.Report(report);
                _writer.WriteInterval(report);
                _verdict.Observe(report);
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 셧다운.
        }
    }

    /// <summary>구간 관측 1회를 수행해 리포트를 만듭니다(샘플러 스레드 전용).</summary>
    internal IntervalReport BuildInterval()
    {
        long nowUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long stallThresholdMs = (long)_options.StallTimeout.TotalMilliseconds;

        int active = 0, authenticated = 0, stalled = 0;
        foreach (VirtualClient client in _clients)
        {
            VirtualClientSnapshot snap = client.ReadSnapshot();
            if (!snap.Connected)
                continue;
            active++;
            if (!snap.Authenticated)
                continue;
            authenticated++;

            // RTT 샘플링: 이벤트가 아닌 구간별 클라이언트 분포 — 판정 목적(백분위 임계치)에 정확하고
            // I/O 스레드에 관측 부담을 전혀 주지 않는다.
            if (snap.RttTicks > 0)
            {
                var rtt = TimeSpan.FromTicks(snap.RttTicks);
                _cumulative.Record(rtt);
                _interval.Record(rtt);
            }

            if (nowUnixMs - snap.LastAppPacketUnixMs > stallThresholdMs)
                stalled++;
        }

        if (authenticated > _peakAuthenticated)
            _peakAuthenticated = authenticated;

        CounterTotals totals = _metrics.SnapshotTotals();
        long broadcastsDelta = totals.Broadcasts - _previousTotals.Broadcasts;
        long bytesDelta = totals.BytesIn - _previousTotals.BytesIn;
        _previousTotals = totals;

        TelemetrySample? tele = _telemetry?.Latest;

        // 전면 스톨: 연결·인증된 전원이 스톨 && 서버는 접속자가 있다고 보고(텔레메트리 미사용 시
        // 클라이언트 관측만으로 판단). 개별 클라이언트 스톨은 리포트만 하고 판정에 넣지 않는다.
        bool fullStall = authenticated > 0 && stalled == authenticated
                         && (tele is null || tele.ConnectedCount > 0);

        RttHistogram intervalSnapshot = _interval.CopyAndReset();

        double? bossHpPct = tele is { BossMaxHp: > 0 }
            ? (double)tele.BossCurrentHp / tele.BossMaxHp * 100.0
            : null;

        var resource = _resources.Sample();

        return new IntervalReport(
            ElapsedSeconds: _elapsed.Elapsed.TotalSeconds,
            Active: active, Target: _options.Clients, Authenticated: authenticated,
            Totals: totals,
            BroadcastsDelta: broadcastsDelta, BytesInDelta: bytesDelta,
            StalledClients: stalled, FullStall: fullStall,
            RttP50Ms: intervalSnapshot.PercentileMs(50),
            RttP95Ms: intervalSnapshot.PercentileMs(95),
            RttP99Ms: intervalSnapshot.PercentileMs(99),
            CumRttP50Ms: _cumulative.PercentileMs(50),
            CumRttP95Ms: _cumulative.PercentileMs(95),
            CumRttP99Ms: _cumulative.PercentileMs(99),
            TeleConnected: tele?.ConnectedCount, TeleRejected: tele?.RejectedConnections,
            TeleGeneration: tele?.Generation, TeleBossHpPct: bossHpPct,
            Resource: resource);
    }

    /// <summary>실행 종료 시 최종 통계를 조립합니다(샘플러 정지 후 메인 스레드에서 호출).</summary>
    public FinalStats BuildFinalStats()
    {
        bool allAuthenticated = true;
        foreach (VirtualClient client in _clients)
        {
            if (!client.ReadSnapshot().EverAuthenticated)
            {
                allAuthenticated = false;
                break;
            }
        }

        return _verdict.BuildFinalStats(
            _cumulative.PercentileMs(50),
            _cumulative.PercentileMs(95),
            _cumulative.PercentileMs(99),
            _metrics.SnapshotTotals(),
            allAuthenticated,
            _elapsed.Elapsed.TotalSeconds);
    }
}
