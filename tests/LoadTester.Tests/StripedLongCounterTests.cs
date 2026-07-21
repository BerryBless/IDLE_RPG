using LoadTester.Metrics;

namespace LoadTester.Tests;

/// <summary><see cref="StripedLongCounter"/>의 정확성(단일/병렬) 검증.</summary>
public class StripedLongCounterTests
{
    [Fact]
    public void 초기값은_0()
    {
        Assert.Equal(0, new StripedLongCounter().Sum());
    }

    [Fact]
    public void 단일스레드_누적합()
    {
        var counter = new StripedLongCounter();
        counter.Add(5);
        counter.Increment();
        counter.Add(10);
        Assert.Equal(16, counter.Sum());
    }

    [Fact]
    public async Task 병렬증가_유실없이_정확히_합산된다()
    {
        var counter = new StripedLongCounter();
        const int Workers = 8;
        const int PerWorker = 100_000;

        var tasks = Enumerable.Range(0, Workers)
            .Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < PerWorker; i++)
                    counter.Increment();
            }));
        await Task.WhenAll(tasks);

        Assert.Equal((long)Workers * PerWorker, counter.Sum());
    }
}
