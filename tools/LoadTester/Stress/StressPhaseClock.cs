namespace LoadTester.Stress;

/// <summary>
/// 경과 시간을 스트레스 페이즈로 매핑하는 순수 시계입니다. Release는 순간 전이라 시간 구간이 아니라
/// During→Recovery 경계의 한 시점으로 취급하며, 러너가 그 시점에 <see cref="IStressScenario.ReleaseAsync"/>를
/// 호출합니다. 전부 순수 계산이라 단위 테스트로 검증합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe(불변). <b>[Blocking:]</b> Non-blocking.
/// </remarks>
public sealed class StressPhaseClock
{
    private readonly double _baselineEnd;
    private readonly double _duringEnd;
    private readonly double _recoveryEnd;

    /// <summary>페이즈 길이로 시계를 만듭니다.</summary>
    public StressPhaseClock(TimeSpan baseline, TimeSpan during, TimeSpan recovery)
    {
        _baselineEnd = baseline.TotalSeconds;
        _duringEnd = _baselineEnd + during.TotalSeconds;
        _recoveryEnd = _duringEnd + recovery.TotalSeconds;
    }

    /// <summary>Baseline 종료 시각(초).</summary>
    public double BaselineEndSeconds => _baselineEnd;

    /// <summary>During 종료 시각(초) = Release/Recovery 시작 경계.</summary>
    public double DuringEndSeconds => _duringEnd;

    /// <summary>전체 종료 시각(초).</summary>
    public double TotalSeconds => _recoveryEnd;

    /// <summary>경과 시각이 속하는 페이즈를 반환합니다. During 종료 경계는 Recovery로 넘어간 것으로 본다.</summary>
    public StressPhase PhaseAt(double elapsedSeconds)
    {
        if (elapsedSeconds < _baselineEnd)
            return StressPhase.Baseline;
        if (elapsedSeconds < _duringEnd)
            return StressPhase.During;
        return StressPhase.Recovery;
    }

    /// <summary>회복 조건 충족 여부(순수 판정): 프로브 RTT p95와 서버 세션 수가 기준선 근처로 복귀했는가.</summary>
    /// <param name="probeRttP95Ms">현재 프로브 RTT p95(ms).</param>
    /// <param name="baselineRttP95Ms">기준선 프로브 RTT p95(ms).</param>
    /// <param name="serverConnected">현재 서버 접속 수.</param>
    /// <param name="baselineServerConnected">기준선 서버 접속 수(≈프로브 수).</param>
    /// <param name="rttMultiplier">허용 RTT 배수(예: 1.5).</param>
    /// <param name="sessionTolerance">허용 세션 수 편차 비율(예: 0.05).</param>
    public static bool IsRecovered(double probeRttP95Ms, double baselineRttP95Ms,
        int serverConnected, int baselineServerConnected, double rttMultiplier, double sessionTolerance)
    {
        bool rttOk = probeRttP95Ms <= Math.Max(baselineRttP95Ms * rttMultiplier, baselineRttP95Ms + 5);
        int allowed = (int)Math.Ceiling(baselineServerConnected * sessionTolerance) + 1;
        bool sessionOk = Math.Abs(serverConnected - baselineServerConnected) <= allowed;
        return rttOk && sessionOk;
    }
}
