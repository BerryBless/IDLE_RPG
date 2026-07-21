using LoadTester.Coordination;
using LoadTester.Options;

namespace LoadTester.Verdict;

/// <summary>용량 판정 임계치입니다.</summary>
/// <param name="TargetConcurrent">목표 동시 연결 수.</param>
/// <param name="MinRetention">램프 완료 후 평균 유지율 하한.</param>
/// <param name="MaxErrorRate">오류율 상한.</param>
/// <param name="ServerMaxWorkingSetMb">서버 워킹셋 상한 MB(null이면 규칙 비활성).</param>
/// <param name="ResourceMonitoringRequested">서버 리소스 모니터링 요청 여부.</param>
public sealed record CapacityThresholds(
    int TargetConcurrent, double MinRetention, double MaxErrorRate,
    int? ServerMaxWorkingSetMb, bool ResourceMonitoringRequested)
{
    /// <summary>실행 옵션에서 용량 임계치를 추출합니다(용량 런은 유지율 0.99·오류율 0.5% 기본).</summary>
    public static CapacityThresholds FromOptions(LoadTestOptions options) => new(
        TargetConcurrent: options.TargetConcurrent ?? options.Clients,
        MinRetention: options.MinRetention,
        MaxErrorRate: Math.Max(options.MaxErrorRate, 0.005),
        ServerMaxWorkingSetMb: options.ServerMaxWorkingSetMb,
        ResourceMonitoringRequested: options.ServerPid is not null || options.ServerProcessName is not null);
}

/// <summary>용량 테스트 최종 통계입니다.</summary>
/// <param name="PeakAuthenticated">관측된 최대 동시 인증 수(전 워커 합).</param>
/// <param name="PeakTeleConnected">관측된 최대 서버 텔레메트리 접속 수(미수신 시 null).</param>
/// <param name="MaxTeleShortfallRatio">구간별 서버 텔레메트리 부족분(클라 대비) 최대 비율.</param>
/// <param name="RetentionMeanAfterRamp">램프 완료 후 평균 유지율.</param>
/// <param name="MinRetentionAfterRamp">램프 완료 후 최저 순간 유지율.</param>
/// <param name="RampCompleted">램프 완료(피크가 0.99×target 도달) 여부.</param>
/// <param name="TotalConnectAttempts">최종 누적 연결 시도.</param>
/// <param name="TotalFailures">최종 누적 실패.</param>
/// <param name="MaxServerWorkingSetMb">관측된 서버 워킹셋 최대(미측정 시 null).</param>
/// <param name="ServerProcessLostObserved">서버 프로세스 소실 관측 여부.</param>
/// <param name="TelemetrySilentTooLong">텔레메트리가 접속자 있는 상태에서 장기 침묵했는지.</param>
/// <param name="AllWorkersHealthy">전 워커가 정상 보고·종료했는지.</param>
/// <param name="ElapsedSeconds">총 실행 초.</param>
public sealed record CapacityStats(
    int PeakAuthenticated, int? PeakTeleConnected, double MaxTeleShortfallRatio,
    double RetentionMeanAfterRamp, double MinRetentionAfterRamp, bool RampCompleted,
    long TotalConnectAttempts, long TotalFailures,
    double? MaxServerWorkingSetMb, bool ServerProcessLostObserved, bool TelemetrySilentTooLong,
    bool AllWorkersHealthy, double ElapsedSeconds);

/// <summary>
/// 대규모 동시 연결 용량 테스트의 PASS/FAIL을 판정합니다. 코디네이터가 <see cref="CombinedInterval"/>을
/// 관찰해 상태를 누적하고, 종료 시 5규칙(피크 도달·유지 안정·오류율·서버 건강·워커 무결성)으로 판정합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Not thread-safe — 코디네이터 집계 스레드 전용. <b>[Blocking:]</b> Non-blocking.
/// </remarks>
public sealed class CapacityVerdictEvaluator
{
    private const int TelemetrySilentIntervalsForFail = 3;

    private readonly CapacityThresholds _thresholds;

    private int _peakAuthenticated;
    private int? _peakTeleConnected;
    // 구간별 서버 텔레메트리 부족분 최대치: 클라가 N 인증인데 서버가 그보다 적게 보고하면(세션 유실
    // 징후) 그 비율을 추적한다. 피크-대-피크는 다른 시점의 최대값이라 유실을 놓치므로 구간별로 본다.
    // 단, 셧다운 전이 구간(워커가 드레인 시작 → 서버 접속 수 급락, 워커 @interval은 아직 직전값)에서
    // 1구간짜리 급락 오탐이 나므로, 2구간 연속 지속될 때만 실제 유실로 집계한다.
    private double _maxTeleShortfallRatio;
    private double _pendingShortfall;
    private int _shortfallStreak;
    private double _retentionSumAfterRamp;
    private double _minRetentionAfterRamp = 1.0;
    private int _intervalsAfterRamp;
    private bool _rampCompleted;
    private long _lastConnectAttempts;
    private long _lastTotalFailures;
    private double? _maxServerWsMb;
    private bool _serverProcessLost;
    private int _telemetrySilentStreak;
    private bool _telemetrySilentTooLong;

    /// <summary>임계치로 판정기를 생성합니다.</summary>
    public CapacityVerdictEvaluator(CapacityThresholds thresholds)
    {
        _thresholds = thresholds;
    }

    /// <summary>지금까지 관측한 최대 동시 인증 수.</summary>
    public int PeakAuthenticated => _peakAuthenticated;

    /// <summary>램프 완료(피크 ≥ 0.99×target) 여부.</summary>
    public bool RampCompleted => _rampCompleted;

    /// <summary>구간 집계 1건을 관찰해 판정 상태를 누적합니다.</summary>
    public void Observe(CombinedInterval interval)
    {
        if (interval.Authenticated > _peakAuthenticated)
            _peakAuthenticated = interval.Authenticated;
        if (interval.TeleConnected is int tc)
        {
            _peakTeleConnected = Math.Max(_peakTeleConnected ?? 0, tc);
            // 서버가 클라 인증 수보다 적게 볼 때만 부족분으로 집계(서버가 더 많이 보는 것은 정상 —
            // 아직 인증 완료 전 세션 등). 이미 어느 정도(목표의 절반 이상) 규모가 찼을 때만 유의미.
            if (interval.Authenticated > _thresholds.TargetConcurrent / 2 && tc < interval.Authenticated)
            {
                double shortfall = (double)(interval.Authenticated - tc) / interval.Authenticated;
                if (shortfall > 0.01)
                {
                    // 2구간 연속 지속 시에만 실제 유실로 확정(1구간짜리 셧다운 전이 급락 무시).
                    _shortfallStreak++;
                    _pendingShortfall = Math.Max(_pendingShortfall, shortfall);
                    if (_shortfallStreak >= 2)
                        _maxTeleShortfallRatio = Math.Max(_maxTeleShortfallRatio, _pendingShortfall);
                }
                else
                {
                    _shortfallStreak = 0;
                    _pendingShortfall = 0;
                }
            }
            else
            {
                _shortfallStreak = 0;
                _pendingShortfall = 0;
            }
        }

        // 램프 완료 판정: 피크 인증이 목표의 99%에 도달하면 그 시점부터 유지율을 집계 시작.
        if (!_rampCompleted && _peakAuthenticated >= 0.99 * _thresholds.TargetConcurrent)
            _rampCompleted = true;

        if (_rampCompleted && _thresholds.TargetConcurrent > 0)
        {
            double retention = (double)interval.Active / _thresholds.TargetConcurrent;
            _retentionSumAfterRamp += retention;
            _intervalsAfterRamp++;
            _minRetentionAfterRamp = Math.Min(_minRetentionAfterRamp, retention);
        }

        // 누적 카운터는 최신값 사용(단조 증가).
        _lastConnectAttempts = interval.ConnectAttempts;
        _lastTotalFailures = interval.TotalFailures;

        if (interval.ServerWorkingSetMb is double ws)
            _maxServerWsMb = Math.Max(_maxServerWsMb ?? 0, ws);
        if (interval.ServerProcessLost)
            _serverProcessLost = true;

        // 텔레메트리 침묵 감시: 접속자가 있는데 텔레메트리가 미수신인 구간이 연속되면 FAIL 후보.
        if (_thresholds.ResourceMonitoringRequested || interval.TeleConnected is not null)
        {
            if (interval.Authenticated > 0 && interval.TeleConnected is null)
            {
                _telemetrySilentStreak++;
                if (_telemetrySilentStreak >= TelemetrySilentIntervalsForFail)
                    _telemetrySilentTooLong = true;
            }
            else
            {
                _telemetrySilentStreak = 0;
            }
        }
    }

    /// <summary>누적 관측을 최종 통계로 조립합니다.</summary>
    /// <param name="allWorkersHealthy">전 워커가 정상 보고·종료했는지.</param>
    /// <param name="elapsedSeconds">총 실행 초.</param>
    public CapacityStats BuildStats(bool allWorkersHealthy, double elapsedSeconds) => new(
        PeakAuthenticated: _peakAuthenticated,
        PeakTeleConnected: _peakTeleConnected,
        MaxTeleShortfallRatio: _maxTeleShortfallRatio,
        RetentionMeanAfterRamp: _intervalsAfterRamp == 0 ? 0 : _retentionSumAfterRamp / _intervalsAfterRamp,
        MinRetentionAfterRamp: _intervalsAfterRamp == 0 ? 0 : _minRetentionAfterRamp,
        RampCompleted: _rampCompleted,
        TotalConnectAttempts: _lastConnectAttempts,
        TotalFailures: _lastTotalFailures,
        MaxServerWorkingSetMb: _maxServerWsMb,
        ServerProcessLostObserved: _serverProcessLost,
        TelemetrySilentTooLong: _telemetrySilentTooLong,
        AllWorkersHealthy: allWorkersHealthy,
        ElapsedSeconds: elapsedSeconds);

    /// <summary>5규칙으로 최종 판정합니다(순수 — 상태는 <paramref name="stats"/>에 이미 반영됨).</summary>
    public Verdict Evaluate(CapacityStats stats)
    {
        var reasons = new List<string>();

        // ① 피크 도달: 램프가 목표에 도달하지 못하면 달성 최대치가 곧 실측 상한.
        if (!stats.RampCompleted || stats.PeakAuthenticated < _thresholds.TargetConcurrent)
        {
            reasons.Add($"목표 동시 연결 미달: 달성 최대 {stats.PeakAuthenticated:N0} / 목표 {_thresholds.TargetConcurrent:N0} " +
                        "(이 수치가 이 환경의 실측 상한)");
        }
        // 교차 검증: 서버 텔레메트리가 클라 인증 수보다 1% 초과 적게 본 구간이 있으면 FAIL(세션 유실 징후).
        if (stats.MaxTeleShortfallRatio > 0.01)
            reasons.Add($"서버 텔레메트리가 클라이언트 인증 수보다 최대 {stats.MaxTeleShortfallRatio:P1} 적게 관측(> 1%, 세션 유실 의심)");

        // ② 유지 안정: 램프 후 평균 유지율 ≥ 하한 && 순간 최저 ≥ 0.97×target.
        if (stats.RampCompleted)
        {
            if (stats.RetentionMeanAfterRamp < _thresholds.MinRetention)
                reasons.Add($"램프 후 평균 유지율 {stats.RetentionMeanAfterRamp:P2} < 하한 {_thresholds.MinRetention:P2}");
            if (stats.MinRetentionAfterRamp < 0.97)
                reasons.Add($"램프 후 순간 최저 유지율 {stats.MinRetentionAfterRamp:P2} < 0.97 (대량 끊김 발생)");
        }

        // ③ 오류율.
        if (stats.TotalConnectAttempts > 0)
        {
            double errorRate = (double)stats.TotalFailures / stats.TotalConnectAttempts;
            if (errorRate > _thresholds.MaxErrorRate)
                reasons.Add($"오류율 {errorRate:P3} > 상한 {_thresholds.MaxErrorRate:P3} " +
                            $"(실패 {stats.TotalFailures:N0}/{stats.TotalConnectAttempts:N0})");
        }

        // ④ 서버 건강.
        if (_thresholds.ResourceMonitoringRequested && stats.ServerProcessLostObserved)
            reasons.Add("모니터링 중 서버 프로세스 소실 관측");
        if (_thresholds.ServerMaxWorkingSetMb is int maxWs && stats.MaxServerWorkingSetMb is double observed && observed > maxWs)
            reasons.Add($"서버 워킹셋 최대 {observed:F0}MB > 상한 {maxWs}MB");
        if (stats.TelemetrySilentTooLong)
            reasons.Add("접속자가 있는 상태에서 서버 텔레메트리가 장기 침묵");

        // ⑤ 워커 무결성.
        if (!stats.AllWorkersHealthy)
            reasons.Add("일부 워커가 정상 보고/종료하지 못함");

        return new Verdict(reasons.Count == 0, reasons);
    }
}
