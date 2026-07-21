using LoadTester.Metrics;
using LoadTester.Verdict;

namespace LoadTester.Tests;

/// <summary><see cref="VerdictEvaluator"/>의 5개 판정 규칙 개별 검증.</summary>
public class VerdictEvaluatorTests
{
    private static VerdictThresholds DefaultThresholds(int? maxWsMb = null, bool monitoring = false) => new(
        RttP50MaxMs: 100, RttP95MaxMs: 250, RttP99MaxMs: 500,
        MinRetention: 0.99, MaxErrorRate: 0.001,
        ServerMaxWorkingSetMb: maxWsMb, ResourceMonitoringRequested: monitoring);

    private static FinalStats HealthyStats(CounterTotals? totals = null) => new(
        CumRttP50Ms: 3, CumRttP95Ms: 11, CumRttP99Ms: 38,
        Totals: totals ?? new CounterTotals(
            ConnectAttempts: 1000, ConnectFailures: 0, AuthSuccesses: 1000, AuthFailures: 0,
            AuthTimeouts: 0, LoginFailures: 0, UnexpectedDisconnects: 0, Reconnects: 0,
            Broadcasts: 100_000, BytesIn: 2_000_000),
        AllClientsEverAuthenticated: true,
        StallIncidents: 0, RetentionMean: 1.0, MaxServerWorkingSetMb: null,
        ServerProcessLostObserved: false, ElapsedSeconds: 60);

    private static IntervalReport Interval(int active, int target, bool fullStall = false,
        double? serverWsMb = null, bool serverLost = false) => new(
        ElapsedSeconds: 10, Active: active, Target: target, Authenticated: active,
        Totals: default, BroadcastsDelta: 0, BytesInDelta: 0,
        StalledClients: fullStall ? active : 0, FullStall: fullStall,
        RttP50Ms: 0, RttP95Ms: 0, RttP99Ms: 0, CumRttP50Ms: 0, CumRttP95Ms: 0, CumRttP99Ms: 0,
        TeleConnected: null, TeleRejected: null, TeleGeneration: null, TeleBossHpPct: null,
        Resource: new ResourceSample(serverWsMb, null, serverLost, 100, 10, 0));

    [Fact]
    public void 정상실행_PASS()
    {
        var evaluator = new VerdictEvaluator(DefaultThresholds());
        var verdict = evaluator.Evaluate(HealthyStats());
        Assert.True(verdict.Passed);
        Assert.Empty(verdict.Reasons);
    }

    [Theory]
    [InlineData(150, 11, 38)]   // p50 초과
    [InlineData(3, 300, 38)]    // p95 초과
    [InlineData(3, 11, 600)]    // p99 초과
    public void 규칙1_RTT임계치_초과시_FAIL(double p50, double p95, double p99)
    {
        var evaluator = new VerdictEvaluator(DefaultThresholds());
        var stats = HealthyStats() with { CumRttP50Ms = p50, CumRttP95Ms = p95, CumRttP99Ms = p99 };
        var verdict = evaluator.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("RTT"));
    }

    [Fact]
    public void 규칙2_오류율초과_FAIL()
    {
        var evaluator = new VerdictEvaluator(DefaultThresholds());
        var totals = new CounterTotals(
            ConnectAttempts: 1000, ConnectFailures: 10, AuthSuccesses: 990, AuthFailures: 0,
            AuthTimeouts: 0, LoginFailures: 0, UnexpectedDisconnects: 0, Reconnects: 0,
            Broadcasts: 0, BytesIn: 0);
        var verdict = evaluator.Evaluate(HealthyStats(totals));
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("오류율"));
    }

    [Fact]
    public void 규칙2_미인증클라이언트존재_FAIL()
    {
        var evaluator = new VerdictEvaluator(DefaultThresholds());
        var stats = HealthyStats() with { AllClientsEverAuthenticated = false };
        var verdict = evaluator.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("인증"));
    }

    [Fact]
    public void 규칙3_유지율_구간관찰로_누적되어_FAIL()
    {
        var evaluator = new VerdictEvaluator(DefaultThresholds());
        // 100/100 5구간 + 50/100 5구간 → 평균 0.75 < 0.99
        for (int i = 0; i < 5; i++)
            evaluator.Observe(Interval(active: 100, target: 100));
        for (int i = 0; i < 5; i++)
            evaluator.Observe(Interval(active: 50, target: 100));

        Assert.Equal(0.75, evaluator.RetentionMean, precision: 5);

        var stats = HealthyStats() with { RetentionMean = evaluator.RetentionMean };
        var verdict = evaluator.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("유지율"));
    }

    [Fact]
    public void 규칙4_전면스톨_2구간연속시_인시던트1회()
    {
        var evaluator = new VerdictEvaluator(DefaultThresholds());

        evaluator.Observe(Interval(100, 100, fullStall: true));
        Assert.Equal(0, evaluator.StallIncidents); // 1구간만으로는 인시던트 아님

        evaluator.Observe(Interval(100, 100, fullStall: true));
        Assert.Equal(1, evaluator.StallIncidents); // 2연속 → 1회

        evaluator.Observe(Interval(100, 100, fullStall: true));
        Assert.Equal(1, evaluator.StallIncidents); // 3구간째는 같은 인시던트 지속

        evaluator.Observe(Interval(100, 100, fullStall: false)); // 해소
        evaluator.Observe(Interval(100, 100, fullStall: true));
        evaluator.Observe(Interval(100, 100, fullStall: true));
        Assert.Equal(2, evaluator.StallIncidents); // 새 스트릭 → 2회

        var stats = HealthyStats() with { StallIncidents = evaluator.StallIncidents };
        Assert.False(evaluator.Evaluate(stats).Passed);
    }

    [Fact]
    public void 규칙5_서버워킹셋_초과시_FAIL()
    {
        var evaluator = new VerdictEvaluator(DefaultThresholds(maxWsMb: 1024, monitoring: true));
        evaluator.Observe(Interval(100, 100, serverWsMb: 800));
        evaluator.Observe(Interval(100, 100, serverWsMb: 1500)); // 최대값 갱신
        evaluator.Observe(Interval(100, 100, serverWsMb: 900));

        Assert.Equal(1500, evaluator.MaxServerWorkingSetMb);

        var stats = HealthyStats() with { MaxServerWorkingSetMb = evaluator.MaxServerWorkingSetMb };
        var verdict = evaluator.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("워킹셋"));
    }

    [Fact]
    public void 규칙5_프로세스소실_모니터링시_FAIL()
    {
        var evaluator = new VerdictEvaluator(DefaultThresholds(monitoring: true));
        evaluator.Observe(Interval(100, 100, serverLost: true));

        var stats = HealthyStats() with { ServerProcessLostObserved = true };
        var verdict = evaluator.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("소실"));
    }

    [Fact]
    public void 규칙5_모니터링미요청시_리소스규칙_비활성()
    {
        var evaluator = new VerdictEvaluator(DefaultThresholds(maxWsMb: 100, monitoring: false));
        var stats = HealthyStats() with { MaxServerWorkingSetMb = 9999, ServerProcessLostObserved = true };
        Assert.True(evaluator.Evaluate(stats).Passed);
    }
}
