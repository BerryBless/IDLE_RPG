using LoadTester.Metrics;
using LoadTester.Options;

namespace LoadTester.Verdict;

/// <summary>PASS/FAIL 판정 임계치 집합입니다(전부 CLI로 재정의 가능).</summary>
/// <param name="RttP50MaxMs">누적 RTT p50 상한(ms).</param>
/// <param name="RttP95MaxMs">누적 RTT p95 상한(ms).</param>
/// <param name="RttP99MaxMs">누적 RTT p99 상한(ms).</param>
/// <param name="MinRetention">구간 평균 연결 유지율 하한.</param>
/// <param name="MaxErrorRate">오류율 상한.</param>
/// <param name="ServerMaxWorkingSetMb">서버 워킹셋 상한 MB(null이면 규칙 비활성).</param>
/// <param name="ResourceMonitoringRequested">서버 리소스 모니터링 요청 여부(소실 판정 활성 조건).</param>
public sealed record VerdictThresholds(
    double RttP50MaxMs, double RttP95MaxMs, double RttP99MaxMs,
    double MinRetention, double MaxErrorRate,
    int? ServerMaxWorkingSetMb, bool ResourceMonitoringRequested)
{
    /// <summary>실행 옵션에서 임계치를 추출합니다.</summary>
    public static VerdictThresholds FromOptions(LoadTestOptions options) => new(
        options.RttP50Max.TotalMilliseconds,
        options.RttP95Max.TotalMilliseconds,
        options.RttP99Max.TotalMilliseconds,
        options.MinRetention,
        options.MaxErrorRate,
        options.ServerMaxWorkingSetMb,
        options.ServerPid is not null || options.ServerProcessName is not null);
}

/// <summary>실행 전체 요약 통계입니다. 판정과 runEnd NDJSON 레코드에 쓰인다.</summary>
/// <param name="CumRttP50Ms">누적 RTT p50(ms).</param>
/// <param name="CumRttP95Ms">누적 RTT p95(ms).</param>
/// <param name="CumRttP99Ms">누적 RTT p99(ms).</param>
/// <param name="Totals">최종 누적 카운터.</param>
/// <param name="AllClientsEverAuthenticated">전 클라이언트가 한 번 이상 인증에 성공했는지.</param>
/// <param name="StallIncidents">전면 스톨 인시던트 횟수(2구간 연속 전면 스톨당 1회).</param>
/// <param name="RetentionMean">구간 평균 연결 유지율(active/target 평균).</param>
/// <param name="MaxServerWorkingSetMb">관측된 서버 워킹셋 최대값(미관측 시 null).</param>
/// <param name="ServerProcessLostObserved">모니터링 중 서버 프로세스 소실이 관측됐는지.</param>
/// <param name="ElapsedSeconds">총 실행 초.</param>
public sealed record FinalStats(
    double CumRttP50Ms, double CumRttP95Ms, double CumRttP99Ms,
    CounterTotals Totals, bool AllClientsEverAuthenticated,
    int StallIncidents, double RetentionMean, double? MaxServerWorkingSetMb,
    bool ServerProcessLostObserved, double ElapsedSeconds);

/// <summary>판정 결과입니다.</summary>
/// <param name="Passed">PASS 여부.</param>
/// <param name="Reasons">FAIL 사유 목록(PASS면 빈 목록).</param>
public sealed record Verdict(bool Passed, IReadOnlyList<string> Reasons);

/// <summary>
/// 구간 리포트를 관찰해 유지율·전면 스톨·서버 리소스 극값을 누적하고, 종료 시
/// 5개 규칙(RTT 임계치·오류율/인증 정합성·유지율·스톨·리소스)으로 PASS/FAIL을 판정합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not thread-safe — 샘플러 스레드 전용
/// (Observe와 상태 누적이 단일 스레드 전제).</description></item>
/// <item><description><b>Memory Allocation:</b> Observe는 무할당. Evaluate 시 사유 리스트만 할당.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 순수 산술 비교만 수행.</description></item>
/// </list>
/// </remarks>
public sealed class VerdictEvaluator
{
    /// <summary>전면 스톨이 인시던트로 승격되는 연속 구간 수.</summary>
    private const int FullStallStreakForIncident = 2;

    private readonly VerdictThresholds _thresholds;

    private double _retentionSum;
    private int _intervalCount;
    private int _fullStallStreak;
    private int _stallIncidents;
    private double? _maxServerWsMb;
    private bool _serverProcessLost;

    /// <summary>임계치로 판정기를 생성합니다.</summary>
    public VerdictEvaluator(VerdictThresholds thresholds)
    {
        _thresholds = thresholds;
    }

    /// <summary>지금까지 관측한 전면 스톨 인시던트 수.</summary>
    public int StallIncidents => _stallIncidents;

    /// <summary>구간 평균 연결 유지율(관측 전 0).</summary>
    public double RetentionMean => _intervalCount == 0 ? 0 : _retentionSum / _intervalCount;

    /// <summary>관측된 서버 워킹셋 최대값(MB).</summary>
    public double? MaxServerWorkingSetMb => _maxServerWsMb;

    /// <summary>모니터링 중 서버 프로세스 소실이 관측됐는지.</summary>
    public bool ServerProcessLostObserved => _serverProcessLost;

    /// <summary>구간 리포트 1건을 관찰해 판정용 상태를 누적합니다.</summary>
    public void Observe(IntervalReport report)
    {
        _retentionSum += report.Target == 0 ? 0 : (double)report.Active / report.Target;
        _intervalCount++;

        if (report.FullStall)
        {
            _fullStallStreak++;
            if (_fullStallStreak == FullStallStreakForIncident)
                _stallIncidents++; // 스트릭당 1회만 집계(3구간째부터는 같은 인시던트의 지속)
        }
        else
        {
            _fullStallStreak = 0;
        }

        if (report.Resource.ServerWorkingSetMb is double ws)
            _maxServerWsMb = Math.Max(_maxServerWsMb ?? 0, ws);
        if (report.Resource.ServerProcessLost)
            _serverProcessLost = true;
    }

    /// <summary>누적 관측 + 최종 집계로 <see cref="FinalStats"/>를 조립합니다.</summary>
    /// <param name="cumP50Ms">누적 RTT p50.</param>
    /// <param name="cumP95Ms">누적 RTT p95.</param>
    /// <param name="cumP99Ms">누적 RTT p99.</param>
    /// <param name="totals">최종 카운터.</param>
    /// <param name="allClientsEverAuthenticated">전 클라이언트 최소 1회 인증 여부.</param>
    /// <param name="elapsedSeconds">총 실행 초.</param>
    public FinalStats BuildFinalStats(double cumP50Ms, double cumP95Ms, double cumP99Ms,
        CounterTotals totals, bool allClientsEverAuthenticated, double elapsedSeconds) => new(
        cumP50Ms, cumP95Ms, cumP99Ms, totals, allClientsEverAuthenticated,
        _stallIncidents, RetentionMean, _maxServerWsMb, _serverProcessLost, elapsedSeconds);

    /// <summary>5개 규칙으로 최종 판정합니다(순수 함수 — 누적 상태는 <paramref name="stats"/>에 이미 반영됨).</summary>
    public Verdict Evaluate(FinalStats stats)
    {
        var reasons = new List<string>();

        // ① 누적 RTT 백분위 임계치
        if (stats.CumRttP50Ms > _thresholds.RttP50MaxMs)
            reasons.Add($"RTT p50 {stats.CumRttP50Ms:F1}ms > 상한 {_thresholds.RttP50MaxMs:F0}ms");
        if (stats.CumRttP95Ms > _thresholds.RttP95MaxMs)
            reasons.Add($"RTT p95 {stats.CumRttP95Ms:F1}ms > 상한 {_thresholds.RttP95MaxMs:F0}ms");
        if (stats.CumRttP99Ms > _thresholds.RttP99MaxMs)
            reasons.Add($"RTT p99 {stats.CumRttP99Ms:F1}ms > 상한 {_thresholds.RttP99MaxMs:F0}ms");

        // ② 응답 정합성: 오류율 + 전원 최소 1회 인증
        if (stats.Totals.TotalAttempts > 0)
        {
            double errorRate = (double)stats.Totals.TotalFailures / stats.Totals.TotalAttempts;
            if (errorRate > _thresholds.MaxErrorRate)
                reasons.Add($"오류율 {errorRate:P3} > 상한 {_thresholds.MaxErrorRate:P3} " +
                            $"(실패 {stats.Totals.TotalFailures}/{stats.Totals.TotalAttempts})");
        }
        if (!stats.AllClientsEverAuthenticated)
            reasons.Add("한 번도 인증에 성공하지 못한 클라이언트가 존재");

        // ③ 연결 유지율
        if (stats.RetentionMean < _thresholds.MinRetention)
            reasons.Add($"평균 연결 유지율 {stats.RetentionMean:P2} < 하한 {_thresholds.MinRetention:P2}");

        // ④ 전면 스톨 인시던트
        if (stats.StallIncidents > 0)
            reasons.Add($"전면 스톨 인시던트 {stats.StallIncidents}회 발생");

        // ⑤ 서버 리소스(모니터링 요청 시에만)
        if (_thresholds.ResourceMonitoringRequested)
        {
            if (stats.ServerProcessLostObserved)
                reasons.Add("모니터링 중 서버 프로세스 소실 관측");
            if (_thresholds.ServerMaxWorkingSetMb is int maxWs &&
                stats.MaxServerWorkingSetMb is double observed && observed > maxWs)
                reasons.Add($"서버 워킹셋 최대 {observed:F0}MB > 상한 {maxWs}MB");
        }

        return new Verdict(reasons.Count == 0, reasons);
    }
}
