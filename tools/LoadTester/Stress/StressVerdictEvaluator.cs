using LoadTester.Verdict;

namespace LoadTester.Stress;

/// <summary>스트레스 판정 임계치입니다.</summary>
/// <param name="ProbeMinConnectedRatioDuring">During 최저 연결 유지율 하한.</param>
/// <param name="ProbeMinAuthedRatioDuring">During 최저 인증 유지율 하한.</param>
/// <param name="ProbeRttDuringMultiplier">During 허용 RTT 배수(기준 대비).</param>
/// <param name="RecoveryRttMultiplier">회복 허용 RTT 배수(기준 대비).</param>
/// <param name="RecoverySessionTolerance">회복 허용 서버 세션 편차 비율.</param>
/// <param name="RecoveryWsMarginRatio">회복 허용 서버 워킹셋 초과 비율.</param>
public sealed record StressThresholds(
    double ProbeMinConnectedRatioDuring = 0.95,
    double ProbeMinAuthedRatioDuring = 0.90,
    double ProbeRttDuringMultiplier = 3.0,
    double RecoveryRttMultiplier = 1.5,
    double RecoverySessionTolerance = 0.05,
    double RecoveryWsMarginRatio = 0.20);

/// <summary>기준선(Baseline 페이즈 평균) 측정치입니다.</summary>
/// <param name="ProbeRttP95Ms">기준선 프로브 RTT p95(ms).</param>
/// <param name="ServerConnected">기준선 서버 접속 수(≈프로브 수).</param>
/// <param name="ServerWsMb">기준선 서버 워킹셋(MB).</param>
public sealed record StressBaseline(double ProbeRttP95Ms, int ServerConnected, double ServerWsMb);

/// <summary>스트레스 실행 최종 통계입니다.</summary>
public sealed record StressStats(
    StressScenarioKind Kind, StressBaseline Baseline,
    double ProbeMinConnectedRatioDuring, double ProbeMinAuthedRatioDuring, double ProbeRttP95During,
    bool ServerAliveAtEnd, bool CrashObserved, bool TelemetryResponsiveAtEnd,
    int PeakServerConnected, double PeakServerWsMb,
    bool SessionCountRecovered, double? TimeToRecoverSeconds,
    double WsGrowthPerStalledPeerKb, long StressConnectFailures, long MalformedFramesSent,
    long StalledHeldPeak, double ElapsedSeconds);

/// <summary>
/// 스트레스 판정기. 용량 판정과 달리 "숫자 도달"이 아니라 <b>생존 + 프로브 건강 + 회복</b>을 본다.
/// 4그룹(생존·프로브 During·회복·리포트)을 시나리오 <see cref="StressExpectations"/> 플래그에 따라
/// 게이트로 적용하거나 리포트만 한다 — slowloris/malformed의 누적은 예상 현상이라 게이트하지 않는다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Not thread-safe — 샘플러 스레드 전용. <b>[Blocking:]</b> Non-blocking(순수 산술).
/// </remarks>
public sealed class StressVerdictEvaluator
{
    private const int TelemetrySilentIntervalsForCrash = 3;

    private readonly StressThresholds _thresholds;
    private readonly StressExpectations _expectations;
    private readonly double _totalSeconds; // Recovery 시작 이후 회복 데드라인 계산용(참고)

    // Baseline 누적(평균).
    private double _baselineRttSum;
    private double _baselineWsSum;
    private long _baselineConnectedSum;
    private int _baselineCount;

    // During 누적.
    private double _duringMinConnectedRatio = 1.0;
    private double _duringMinAuthedRatio = 1.0;
    private double _duringMaxRttP95;

    // 생존/크래시.
    private bool _crashObserved;
    private bool _serverAliveAtEnd = true;
    private bool _telemetryResponsiveAtEnd;
    private int _telemetrySilentStreak;

    // 피크/회복.
    private int _peakServerConnected;
    private double _peakServerWsMb;
    private long _stalledHeldPeak;
    private bool _sessionCountRecovered;
    private double? _timeToRecoverSeconds;
    private double _recoveryStartSeconds = -1;

    private StressBaseline? _baseline;

    /// <summary>판정기를 생성합니다.</summary>
    public StressVerdictEvaluator(StressThresholds thresholds, StressExpectations expectations, double totalSeconds)
    {
        _thresholds = thresholds;
        _expectations = expectations;
        _totalSeconds = totalSeconds;
    }

    /// <summary>확정된 기준선(Baseline 페이즈 종료 시 계산). 아직 없으면 null.</summary>
    public StressBaseline? Baseline => _baseline;

    /// <summary>구간 관측 1건을 반영합니다.</summary>
    public void Observe(StressIntervalReport r)
    {
        // 리소스·텔레메트리 공통.
        if (r.Resource.ServerProcessLost)
        {
            _crashObserved = true;
            _serverAliveAtEnd = false;
        }
        else
        {
            _serverAliveAtEnd = true;
        }

        bool probeActive = r.Probe.Authenticated > 0;
        if (r.TeleConnected is not null)
        {
            _telemetrySilentStreak = 0;
            _telemetryResponsiveAtEnd = true;
        }
        else if (probeActive)
        {
            _telemetrySilentStreak++;
            _telemetryResponsiveAtEnd = false;
            if (_telemetrySilentStreak >= TelemetrySilentIntervalsForCrash)
                _crashObserved = true;
        }

        if (r.TeleConnected is int tc)
            _peakServerConnected = Math.Max(_peakServerConnected, tc);
        if (r.Resource.ServerWorkingSetMb is double ws)
            _peakServerWsMb = Math.Max(_peakServerWsMb, ws);
        _stalledHeldPeak = Math.Max(_stalledHeldPeak, r.Driver.StalledHeld);

        switch (r.Phase)
        {
            case StressPhase.Baseline:
                _baselineRttSum += r.Probe.RttP95Ms;
                if (r.Resource.ServerWorkingSetMb is double bws) _baselineWsSum += bws;
                _baselineConnectedSum += r.TeleConnected ?? r.Probe.Authenticated;
                _baselineCount++;
                break;

            case StressPhase.During:
                EnsureBaseline();
                if (r.Probe.Size > 0)
                {
                    _duringMinConnectedRatio = Math.Min(_duringMinConnectedRatio, (double)r.Probe.Connected / r.Probe.Size);
                    double authDenom = Math.Max(1, r.Probe.EverAuthenticated);
                    _duringMinAuthedRatio = Math.Min(_duringMinAuthedRatio, r.Probe.Authenticated / authDenom);
                }
                _duringMaxRttP95 = Math.Max(_duringMaxRttP95, r.Probe.RttP95Ms);
                break;

            case StressPhase.Recovery:
                EnsureBaseline();
                if (_recoveryStartSeconds < 0)
                    _recoveryStartSeconds = r.ElapsedSeconds;
                if (!_sessionCountRecovered && _baseline is not null && r.TeleConnected is int rc)
                {
                    if (StressPhaseClock.IsRecovered(r.Probe.RttP95Ms, _baseline.ProbeRttP95Ms,
                            rc, _baseline.ServerConnected, _thresholds.RecoveryRttMultiplier, _thresholds.RecoverySessionTolerance))
                    {
                        _sessionCountRecovered = true;
                        _timeToRecoverSeconds = r.ElapsedSeconds - _recoveryStartSeconds;
                    }
                }
                break;
        }
    }

    private void EnsureBaseline()
    {
        if (_baseline is not null || _baselineCount == 0)
            return;
        _baseline = new StressBaseline(
            ProbeRttP95Ms: _baselineRttSum / _baselineCount,
            ServerConnected: (int)(_baselineConnectedSum / _baselineCount),
            ServerWsMb: _baselineWsSum / _baselineCount);
    }

    /// <summary>누적 관측을 최종 통계로 조립합니다.</summary>
    /// <param name="kind">시나리오 종류.</param>
    /// <param name="lastDriver">마지막 드라이버 스냅샷(연결 실패·악성 프레임 총계).</param>
    /// <param name="elapsedSeconds">총 실행 초.</param>
    public StressStats BuildStats(StressScenarioKind kind, StressDriverSnapshot lastDriver, double elapsedSeconds)
    {
        EnsureBaseline();
        var baseline = _baseline ?? new StressBaseline(0, 0, 0);
        double wsGrowthKb = _stalledHeldPeak > 0
            ? Math.Max(0, _peakServerWsMb - baseline.ServerWsMb) * 1024.0 / _stalledHeldPeak
            : 0;
        return new StressStats(
            Kind: kind, Baseline: baseline,
            ProbeMinConnectedRatioDuring: _duringMinConnectedRatio,
            ProbeMinAuthedRatioDuring: _duringMinAuthedRatio,
            ProbeRttP95During: _duringMaxRttP95,
            ServerAliveAtEnd: _serverAliveAtEnd, CrashObserved: _crashObserved,
            TelemetryResponsiveAtEnd: _telemetryResponsiveAtEnd,
            PeakServerConnected: _peakServerConnected, PeakServerWsMb: _peakServerWsMb,
            SessionCountRecovered: _sessionCountRecovered, TimeToRecoverSeconds: _timeToRecoverSeconds,
            WsGrowthPerStalledPeerKb: wsGrowthKb,
            StressConnectFailures: lastDriver.StressConnectFailures,
            MalformedFramesSent: lastDriver.MalformedFramesSent,
            StalledHeldPeak: _stalledHeldPeak,
            ElapsedSeconds: elapsedSeconds);
    }

    /// <summary>생존·프로브 건강(하드 게이트) + 회복(기대 시에만 게이트)으로 최종 판정합니다.</summary>
    public Verdict.Verdict Evaluate(StressStats s)
    {
        var reasons = new List<string>();

        // ① 생존(하드 게이트).
        if (s.CrashObserved || !s.ServerAliveAtEnd)
            reasons.Add("서버 크래시/프로세스 소실 관측(생존 실패)");

        // ② 프로브 건강 During(하드 게이트).
        if (s.ProbeMinConnectedRatioDuring < _thresholds.ProbeMinConnectedRatioDuring)
            reasons.Add($"과부하 중 프로브 연결 유지율 {s.ProbeMinConnectedRatioDuring:P1} < 하한 {_thresholds.ProbeMinConnectedRatioDuring:P0}");
        if (s.ProbeMinAuthedRatioDuring < _thresholds.ProbeMinAuthedRatioDuring)
            reasons.Add($"과부하 중 프로브 인증 유지율 {s.ProbeMinAuthedRatioDuring:P1} < 하한 {_thresholds.ProbeMinAuthedRatioDuring:P0}");
        double rttGate = Math.Max(s.Baseline.ProbeRttP95Ms * _thresholds.ProbeRttDuringMultiplier, s.Baseline.ProbeRttP95Ms + 50);
        if (s.Baseline.ProbeRttP95Ms > 0 && s.ProbeRttP95During > rttGate)
            reasons.Add($"과부하 중 프로브 RTT p95 {s.ProbeRttP95During:F1}ms > 허용 {rttGate:F1}ms");

        // ③ 회복(기대 시에만 게이트).
        if (_expectations.ExpectSessionCountRecovery && !s.SessionCountRecovered)
            reasons.Add($"해제 후 서버 세션 수/RTT가 기준선으로 회복되지 않음(피크 접속 {s.PeakServerConnected:N0})");
        if (_expectations.ExpectServerWsRecovery && s.Baseline.ServerWsMb > 0
            && s.PeakServerWsMb > s.Baseline.ServerWsMb * (1 + _thresholds.RecoveryWsMarginRatio)
            && !s.SessionCountRecovered)
            reasons.Add($"해제 후 서버 워킹셋이 기준선으로 회복되지 않음(피크 {s.PeakServerWsMb:F0}MB)");

        return new Verdict.Verdict(reasons.Count == 0, reasons);
    }
}
