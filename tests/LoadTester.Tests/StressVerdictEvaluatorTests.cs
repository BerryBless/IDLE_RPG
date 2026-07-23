using LoadTester.Metrics;
using LoadTester.Stress;

namespace LoadTester.Tests;

/// <summary><see cref="StressVerdictEvaluator"/>의 생존·프로브·회복 게이트 검증(시나리오별 기대 플래그).</summary>
public class StressVerdictEvaluatorTests
{
    private static StressExpectations Recoverable => new(true, true, "회복 기대");
    private static StressExpectations Accumulating => new(false, false, "누적 기대");

    private static ResourceSample Res(double srvWs, bool lost = false) =>
        new(srvWs, 5.0, lost, SelfWorkingSetMb: 100, SelfThreadCount: 10, SelfGen2Collections: 0);

    private static StressIntervalReport Report(StressPhase phase, double t, int probeSize, int connected,
        int authed, double rttP95, int? tele, double srvWs, long stalled = 0, bool srvLost = false) =>
        new(phase, t,
            new ProbeHealthSnapshot(probeSize, connected, authed, probeSize, rttP95 * 0.5, rttP95),
            new StressDriverSnapshot(0, 0, stalled, 0, 0, stalled),
            tele, 0, Res(srvWs, srvLost));

    private static void FeedBaseline(StressVerdictEvaluator e, int probe, double rtt, int tele, double ws)
    {
        for (int i = 0; i < 3; i++)
            e.Observe(Report(StressPhase.Baseline, i * 2, probe, probe, probe, rtt, tele, ws));
    }

    [Fact]
    public void 버스트_정상회복_PASS()
    {
        var e = new StressVerdictEvaluator(new StressThresholds(), Recoverable, totalSeconds: 200);
        FeedBaseline(e, probe: 200, rtt: 2.0, tele: 200, ws: 300);
        // During: 프로브 유지, 서버 접속 폭증
        for (int i = 0; i < 5; i++)
            e.Observe(Report(StressPhase.During, 30 + i * 2, 200, 200, 200, 4.0, 80000, 1500));
        // Recovery: 서버 접속·RTT 기준선 복귀
        for (int i = 0; i < 5; i++)
            e.Observe(Report(StressPhase.Recovery, 90 + i * 2, 200, 200, 200, 2.5, 200, 320));

        var stats = e.BuildStats(StressScenarioKind.Burst, default, 130);
        var verdict = e.Evaluate(stats);
        Assert.True(verdict.Passed, string.Join("; ", verdict.Reasons));
        Assert.True(stats.SessionCountRecovered);
    }

    [Fact]
    public void 버스트_세션누수_회복실패_FAIL()
    {
        var e = new StressVerdictEvaluator(new StressThresholds(), Recoverable, totalSeconds: 200);
        FeedBaseline(e, 200, 2.0, 200, 300);
        for (int i = 0; i < 5; i++)
            e.Observe(Report(StressPhase.During, 30 + i * 2, 200, 200, 200, 4.0, 80000, 1500));
        // Recovery: 서버 접속이 5만에서 안 내려감(누수)
        for (int i = 0; i < 5; i++)
            e.Observe(Report(StressPhase.Recovery, 90 + i * 2, 200, 200, 200, 2.5, 50000, 1500));

        var stats = e.BuildStats(StressScenarioKind.Burst, default, 130);
        var verdict = e.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("회복"));
    }

    [Fact]
    public void Slowloris_무한누적이어도_생존하면_PASS()
    {
        var e = new StressVerdictEvaluator(new StressThresholds(), Accumulating, totalSeconds: 200);
        FeedBaseline(e, 200, 2.0, 200, 300);
        // During: 정체 피어 누적, 프로브는 건강
        for (int i = 0; i < 5; i++)
            e.Observe(Report(StressPhase.During, 30 + i * 2, 200, 200, 200, 3.0, 4200, 600, stalled: 4000));
        // Recovery: 서버 접속 안 내려감(설계상 누적) — 그래도 게이트 아님
        for (int i = 0; i < 5; i++)
            e.Observe(Report(StressPhase.Recovery, 90 + i * 2, 200, 200, 200, 2.2, 4200, 600, stalled: 4000));

        var stats = e.BuildStats(StressScenarioKind.Slowloris, default, 130);
        var verdict = e.Evaluate(stats);
        Assert.True(verdict.Passed, string.Join("; ", verdict.Reasons)); // 누적은 리포트만, 게이트 아님
        Assert.False(stats.SessionCountRecovered);
    }

    [Fact]
    public void 서버크래시_FAIL()
    {
        var e = new StressVerdictEvaluator(new StressThresholds(), Accumulating, totalSeconds: 200);
        FeedBaseline(e, 200, 2.0, 200, 300);
        e.Observe(Report(StressPhase.During, 30, 200, 200, 200, 3.0, null, 0, srvLost: true));

        var stats = e.BuildStats(StressScenarioKind.Malformed, default, 40);
        var verdict = e.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("크래시") || r.Contains("소실"));
    }

    [Fact]
    public void 과부하중_프로브_붕괴_FAIL()
    {
        var e = new StressVerdictEvaluator(new StressThresholds(), Recoverable, totalSeconds: 200);
        FeedBaseline(e, 200, 2.0, 200, 300);
        // During: 프로브 연결이 50%로 붕괴
        e.Observe(Report(StressPhase.During, 30, 200, 100, 100, 4.0, 80000, 1500));

        var stats = e.BuildStats(StressScenarioKind.Burst, default, 40);
        var verdict = e.Evaluate(stats);
        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Reasons, r => r.Contains("프로브"));
    }
}
