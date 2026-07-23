using LoadTester.Client;

namespace LoadTester.Tests;

/// <summary><see cref="ReconnectPolicy"/>의 지수 백오프·상한·지터 범위 검증(시드 고정으로 결정적).</summary>
public class ReconnectPolicyTests
{
    [Theory]
    [InlineData(1, 3_000)]
    [InlineData(2, 6_000)]
    [InlineData(3, 12_000)]
    [InlineData(4, 24_000)]
    [InlineData(5, 48_000)]
    public void 지수백오프_지터범위내(int attempt, double expectedBaseMs)
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(3), new Random(42));
        var delay = policy.NextDelay(attempt);
        Assert.InRange(delay.TotalMilliseconds, expectedBaseMs * 0.8, expectedBaseMs * 1.2);
    }

    [Theory]
    [InlineData(6)]   // 3s×2^5=96s > cap
    [InlineData(10)]
    [InlineData(100)] // 오버플로 방지 경로
    public void 상한60초_지터포함_72초를_넘지않는다(int attempt)
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(3), new Random(7));
        var delay = policy.NextDelay(attempt);
        Assert.InRange(delay.TotalMilliseconds, 60_000 * 0.8, 60_000 * 1.2);
    }

    [Fact]
    public void attempt_0이하는_1회차로_보정된다()
    {
        var policy = new ReconnectPolicy(TimeSpan.FromSeconds(3), new Random(1));
        Assert.InRange(policy.NextDelay(0).TotalMilliseconds, 3_000 * 0.8, 3_000 * 1.2);
        Assert.InRange(policy.NextDelay(-5).TotalMilliseconds, 3_000 * 0.8, 3_000 * 1.2);
    }

    [Fact]
    public void 같은시드_같은지연_재현성()
    {
        var a = new ReconnectPolicy(TimeSpan.FromSeconds(3), new Random(99));
        var b = new ReconnectPolicy(TimeSpan.FromSeconds(3), new Random(99));
        Assert.Equal(a.NextDelay(2), b.NextDelay(2));
    }
}
