using LoadTester.Stress;

namespace LoadTester.Tests;

/// <summary><see cref="StressPhaseClock"/>의 페이즈 전이·회복 판정 검증.</summary>
public class StressPhaseClockTests
{
    private static StressPhaseClock Clock() =>
        new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90));

    [Theory]
    [InlineData(0, StressPhase.Baseline)]
    [InlineData(29.9, StressPhase.Baseline)]
    [InlineData(30, StressPhase.During)]
    [InlineData(89.9, StressPhase.During)]
    [InlineData(90, StressPhase.Recovery)]
    [InlineData(179, StressPhase.Recovery)]
    public void 페이즈전이_경계(double elapsed, StressPhase expected)
    {
        Assert.Equal(expected, Clock().PhaseAt(elapsed));
    }

    [Fact]
    public void 경계시각()
    {
        var c = Clock();
        Assert.Equal(30, c.BaselineEndSeconds);
        Assert.Equal(90, c.DuringEndSeconds);
        Assert.Equal(180, c.TotalSeconds);
    }

    [Fact]
    public void 회복판정_RTT와세션_둘다_충족해야_true()
    {
        // 기준 RTT 2ms, 기준 접속 200. 배수 1.5, 허용 5%.
        Assert.True(StressPhaseClock.IsRecovered(2.5, 2.0, 205, 200, 1.5, 0.05));   // 둘 다 OK
        Assert.False(StressPhaseClock.IsRecovered(10.0, 2.0, 200, 200, 1.5, 0.05)); // RTT 초과
        Assert.False(StressPhaseClock.IsRecovered(2.5, 2.0, 5000, 200, 1.5, 0.05)); // 세션 누수
    }

    [Fact]
    public void 회복판정_RTT_기준0일때_절대여유()
    {
        // 기준 RTT 0.5ms → max(0.75, 5.5) = 5.5ms 허용
        Assert.True(StressPhaseClock.IsRecovered(5.0, 0.5, 200, 200, 1.5, 0.05));
    }
}
