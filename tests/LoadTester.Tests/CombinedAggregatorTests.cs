using LoadTester.Coordination;
using LoadTester.Metrics;

namespace LoadTester.Tests;

/// <summary><see cref="CombinedAggregator"/>의 라인 흡수·합산·무결성 카운트 검증(텔레메트리 없이).</summary>
public class CombinedAggregatorTests
{
    private static CombinedAggregator NewAggregator(int workers, int target) =>
        new(workers, target, telemetry: null, resources: new ResourceMonitor(null, null));

    [Fact]
    public void 세워커_구간합산()
    {
        var agg = NewAggregator(workers: 3, target: 300);
        agg.OfferLine(WorkerLineProtocol.FormatInterval(new WorkerStatus(0, 10, 100, 100, 99, 100, 1, 0, 0, 0, 50)));
        agg.OfferLine(WorkerLineProtocol.FormatInterval(new WorkerStatus(1, 10, 100, 100, 100, 100, 0, 1, 0, 0, 60)));
        agg.OfferLine(WorkerLineProtocol.FormatInterval(new WorkerStatus(2, 10, 90, 100, 90, 95, 5, 0, 2, 0, 55)));

        var combined = agg.BuildCombined(10);
        Assert.Equal(3, combined.WorkersReporting);
        Assert.Equal(290, combined.Active);
        Assert.Equal(289, combined.Authenticated);
        Assert.Equal(295, combined.ConnectAttempts);
        Assert.Equal(6, combined.TotalFailures);
        Assert.Equal(1, combined.UnexpectedDisconnects);
        Assert.Equal(60, combined.MaxWorkerWorkingSetMb);
    }

    [Fact]
    public void 최신값만_유지_구버전_대체()
    {
        var agg = NewAggregator(workers: 1, target: 100);
        agg.OfferLine(WorkerLineProtocol.FormatInterval(new WorkerStatus(0, 10, 50, 100, 50, 50, 0, 0, 0, 0, 30)));
        agg.OfferLine(WorkerLineProtocol.FormatInterval(new WorkerStatus(0, 20, 100, 100, 100, 100, 0, 0, 0, 0, 40)));

        var combined = agg.BuildCombined(20);
        Assert.Equal(100, combined.Active); // 최신값
        Assert.Equal(1, combined.WorkersReporting);
    }

    [Fact]
    public void Final과_보고카운트()
    {
        var agg = NewAggregator(workers: 2, target: 200);
        Assert.Equal(0, agg.ReportingWorkerCount);

        agg.OfferLine(WorkerLineProtocol.FormatInterval(new WorkerStatus(0, 10, 100, 100, 100, 100, 0, 0, 0, 0, 50)));
        Assert.Equal(1, agg.ReportingWorkerCount);
        Assert.False(agg.HasFinal(0));

        agg.OfferLine(WorkerLineProtocol.FormatFinal(new WorkerFinal(0, true, 100, 100, 0, "normal")));
        Assert.True(agg.HasFinal(0));
        Assert.Equal(1, agg.FinalCount);
        Assert.Equal(1, agg.ReportingWorkerCount); // 이미 보고한 워커라 카운트 유지
    }

    [Fact]
    public void 사람용라인은_흡수하지않음()
    {
        var agg = NewAggregator(workers: 1, target: 100);
        Assert.False(agg.OfferLine("[w0] 사람용 로그"));
        Assert.True(agg.OfferLine(WorkerLineProtocol.FormatInterval(new WorkerStatus(0, 10, 1, 1, 1, 1, 0, 0, 0, 0, 10))));
    }
}
