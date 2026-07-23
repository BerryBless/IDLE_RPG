using LoadTester.Metrics;

namespace LoadTester.Tests;

/// <summary><see cref="RttHistogram"/>의 버킷 경계·백분위·리셋 검증.</summary>
public class RttHistogramTests
{
    [Theory]
    [InlineData(-1, 0)]          // 음수 → 첫 버킷
    [InlineData(0, 0)]           // 밴드1 시작
    [InlineData(0.05, 0)]
    [InlineData(0.1, 1)]         // 0.1ms 해상도
    [InlineData(19.95, 199)]     // 밴드1 마지막
    [InlineData(20, 200)]        // 밴드2 시작(1ms 해상도)
    [InlineData(21, 201)]
    [InlineData(199, 379)]       // 밴드2 마지막
    [InlineData(200, 380)]       // 밴드3 시작(10ms 해상도)
    [InlineData(1999, 559)]      // 밴드3 마지막
    [InlineData(2000, 560)]      // 밴드4 시작(250ms 해상도)
    [InlineData(59999, 791)]     // 밴드4 마지막
    [InlineData(60000, 792)]     // 오버플로
    [InlineData(1000000, 792)]
    public void 버킷인덱스_경계값(double ms, int expectedIndex)
    {
        Assert.Equal(expectedIndex, RttHistogram.BucketIndex(ms));
    }

    [Fact]
    public void 빈히스토그램_백분위0()
    {
        var histogram = new RttHistogram();
        Assert.Equal(0, histogram.PercentileMs(50));
        Assert.Equal(0, histogram.TotalCount);
    }

    [Fact]
    public void 백분위_균등분포에서_정확한_버킷을_찾는다()
    {
        var histogram = new RttHistogram();
        // 1..100ms 각 1건 → p50 ≈ 50ms, p99 ≈ 99ms (버킷 대표값은 구간 중앙이라 ±해상도/2 오차)
        for (int ms = 1; ms <= 100; ms++)
            histogram.Record(TimeSpan.FromMilliseconds(ms));

        Assert.Equal(100, histogram.TotalCount);
        Assert.InRange(histogram.PercentileMs(50), 49, 51);
        Assert.InRange(histogram.PercentileMs(99), 98, 100);
        Assert.InRange(histogram.PercentileMs(100), 99, 101);
    }

    [Fact]
    public void 백분위_저지연분포_고해상도로_구분된다()
    {
        var histogram = new RttHistogram();
        // 3.1ms 90건 + 15ms 10건 → p50=3.1ms대(0.1ms 해상도), p95=15ms대
        for (int i = 0; i < 90; i++)
            histogram.Record(TimeSpan.FromMilliseconds(3.1));
        for (int i = 0; i < 10; i++)
            histogram.Record(TimeSpan.FromMilliseconds(15));

        Assert.InRange(histogram.PercentileMs(50), 3.0, 3.25);
        Assert.InRange(histogram.PercentileMs(95), 14.9, 15.2);
    }

    [Fact]
    public void 잘못된_백분위_예외()
    {
        var histogram = new RttHistogram();
        histogram.Record(TimeSpan.FromMilliseconds(5));
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.PercentileMs(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => histogram.PercentileMs(101));
    }

    [Fact]
    public void CopyAndReset_사본은_유지되고_원본은_비워진다()
    {
        var histogram = new RttHistogram();
        histogram.Record(TimeSpan.FromMilliseconds(10));
        histogram.Record(TimeSpan.FromMilliseconds(20));

        var copy = histogram.CopyAndReset();

        Assert.Equal(2, copy.TotalCount);
        Assert.Equal(0, histogram.TotalCount);
        Assert.Equal(0, histogram.PercentileMs(50));
        Assert.True(copy.PercentileMs(50) > 0);
    }

    [Fact]
    public void 오버플로샘플_60초로_보고된다()
    {
        var histogram = new RttHistogram();
        histogram.Record(TimeSpan.FromMinutes(5));
        Assert.Equal(60_000, histogram.PercentileMs(50));
    }
}
