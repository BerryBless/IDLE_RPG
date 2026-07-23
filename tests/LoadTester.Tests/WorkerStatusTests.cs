using LoadTester.Coordination;

namespace LoadTester.Tests;

/// <summary><see cref="WorkerLineProtocol"/> 왕복·거부 검증.</summary>
public class WorkerStatusTests
{
    [Fact]
    public void Interval_왕복()
    {
        var status = new WorkerStatus(3, 12.5, 40000, 40000, 39998, 40001, 3, 2, 5, 1, 210.5);
        string line = WorkerLineProtocol.FormatInterval(status);
        Assert.StartsWith("@interval ", line);
        Assert.True(WorkerLineProtocol.TryParseInterval(line, out var parsed));
        Assert.Equal(status, parsed);
    }

    [Fact]
    public void Final_왕복()
    {
        var final = new WorkerFinal(1, true, 37500, 37600, 100, "normal");
        string line = WorkerLineProtocol.FormatFinal(final);
        Assert.StartsWith("@final ", line);
        Assert.True(WorkerLineProtocol.TryParseFinal(line, out var parsed));
        Assert.Equal(final, parsed);
    }

    [Theory]
    [InlineData("[w3] 사람용 로그 라인")]
    [InlineData("@interval not-json")]
    [InlineData("")]
    [InlineData("random text")]
    public void 비정상라인_Interval파싱_거부(string line)
    {
        Assert.False(WorkerLineProtocol.TryParseInterval(line, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void Interval라인은_Final파서가_거부()
    {
        string interval = WorkerLineProtocol.FormatInterval(new WorkerStatus(0, 1, 1, 1, 1, 1, 0, 0, 0, 0, 10));
        Assert.False(WorkerLineProtocol.TryParseFinal(interval, out _));
    }
}
