using LoadTester.Coordination;
using LoadTester.Verdict;

namespace LoadTester.Tests;

/// <summary><see cref="CapacityVerdictEvaluator"/> 5규칙 검증.</summary>
public class CapacityVerdictEvaluatorTests
{
    private static CapacityThresholds Thresholds(int target = 1000, int? maxWs = null, bool monitoring = false) =>
        new(target, MinRetention: 0.99, MaxErrorRate: 0.005, ServerMaxWorkingSetMb: maxWs, ResourceMonitoringRequested: monitoring);

    private static CombinedInterval Interval(double elapsed, int active, int authenticated, int target,
        long attempts = 0, long failures = 0, int? tele = null, double? srvWs = null, bool srvLost = false) =>
        new(elapsed, WorkersReporting: 4, TotalWorkers: 4, Active: active, Target: target,
            Authenticated: authenticated, ConnectAttempts: attempts, TotalFailures: failures,
            UnexpectedDisconnects: 0, Reconnects: 0, StalledClients: 0, MaxWorkerWorkingSetMb: 100,
            TeleConnected: tele, TeleRejected: 0, ServerWorkingSetMb: srvWs, ServerCpuPercent: null,
            ServerProcessLost: srvLost);

    [Fact]
    public void 목표도달_유지안정_PASS()
    {
        var e = new CapacityVerdictEvaluator(Thresholds(target: 1000));
        // 램프: 0→1000, 이후 5구간 1000 유지, 시도 1000 실패 0
        e.Observe(Interval(10, 500, 500, 1000, 1000, 0));
        e.Observe(Interval(20, 1000, 1000, 1000, 1000, 0));
        for (int i = 0; i < 5; i++)
            e.Observe(Interval(30 + i * 10, 1000, 1000, 1000, 1000, 0));

        var stats = e.BuildStats(allWorkersHealthy: true, elapsedSeconds: 80);
        var verdict = e.Evaluate(stats);
        Assert.True(verdict.Passed, string.Join("; ", verdict.Reasons));
        Assert.Equal(1000, stats.PeakAuthenticated);
        Assert.True(stats.RampCompleted);
    }

    [Fact]
    public void 목표미달_FAIL_사유에_달성최대치()
    {
        var e = new CapacityVerdictEvaluator(Thresholds(target: 1000));
        // 최대 700까지만 도달
        e.Observe(Interval(10, 500, 500, 1000, 500, 0));
        e.Observe(Interval(20, 700, 700, 1000, 700, 0));
        e.Observe(Interval(30, 680, 680, 1000, 700, 0));

        var stats = e.BuildStats(true, 40);
        var verdict = e.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.False(stats.RampCompleted);
        Assert.Equal(700, stats.PeakAuthenticated);
        Assert.Contains(verdict.Reasons, r => r.Contains("700") && r.Contains("실측 상한"));
    }

    [Fact]
    public void 램프후_대량끊김_FAIL()
    {
        var e = new CapacityVerdictEvaluator(Thresholds(target: 1000));
        e.Observe(Interval(10, 1000, 1000, 1000, 1000, 0));
        e.Observe(Interval(20, 1000, 1000, 1000, 1000, 0));
        e.Observe(Interval(30, 900, 900, 1000, 1000, 0)); // 90% < 0.97 순간 최저

        var stats = e.BuildStats(true, 40);
        var verdict = e.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("순간 최저"));
    }

    [Fact]
    public void 오류율초과_FAIL()
    {
        var e = new CapacityVerdictEvaluator(Thresholds(target: 1000));
        e.Observe(Interval(10, 1000, 1000, 1000, attempts: 1000, failures: 0));
        e.Observe(Interval(20, 1000, 1000, 1000, attempts: 2000, failures: 20)); // 1% > 0.5%

        var stats = e.BuildStats(true, 30);
        var verdict = e.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("오류율"));
    }

    [Fact]
    public void 텔레메트리_2구간연속_불일치_FAIL()
    {
        var e = new CapacityVerdictEvaluator(Thresholds(target: 1000));
        e.Observe(Interval(10, 1000, 1000, 1000, 1000, 0, tele: 1000));
        e.Observe(Interval(20, 1000, 1000, 1000, 1000, 0, tele: 900)); // 부족분 1구간(streak 1)
        e.Observe(Interval(30, 1000, 1000, 1000, 1000, 0, tele: 900)); // 2구간 연속 → 확정

        var stats = e.BuildStats(true, 40);
        var verdict = e.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("텔레메트리"));
    }

    [Fact]
    public void 텔레메트리_1구간_셧다운급락_오탐없이_PASS()
    {
        var e = new CapacityVerdictEvaluator(Thresholds(target: 1000));
        for (int i = 0; i < 5; i++)
            e.Observe(Interval(10 + i * 10, 1000, 1000, 1000, 1000, 0, tele: 1000));
        // 마지막 셧다운 전이 틱: 워커는 아직 1000, 서버 텔레메트리는 드레인으로 급락(1구간뿐)
        e.Observe(Interval(60, 1000, 1000, 1000, 1000, 0, tele: 350));

        var stats = e.BuildStats(true, 70);
        var verdict = e.Evaluate(stats);
        Assert.True(verdict.Passed, string.Join("; ", verdict.Reasons));
    }

    [Fact]
    public void 서버워킹셋초과_FAIL()
    {
        var e = new CapacityVerdictEvaluator(Thresholds(target: 1000, maxWs: 1024, monitoring: true));
        e.Observe(Interval(10, 1000, 1000, 1000, 1000, 0, srvWs: 800));
        e.Observe(Interval(20, 1000, 1000, 1000, 1000, 0, srvWs: 1500));

        var stats = e.BuildStats(true, 30);
        var verdict = e.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("워킹셋"));
    }

    [Fact]
    public void 워커무결성_실패_FAIL()
    {
        var e = new CapacityVerdictEvaluator(Thresholds(target: 1000));
        e.Observe(Interval(10, 1000, 1000, 1000, 1000, 0));
        e.Observe(Interval(20, 1000, 1000, 1000, 1000, 0));

        var stats = e.BuildStats(allWorkersHealthy: false, elapsedSeconds: 30);
        var verdict = e.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("워커"));
    }
}
