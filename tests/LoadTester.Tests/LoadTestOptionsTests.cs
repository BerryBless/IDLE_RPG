using LoadTester.Options;

namespace LoadTester.Tests;

/// <summary><see cref="LoadTestOptions.TryParse"/>와 기간 파서의 단위 검증.</summary>
public class LoadTestOptionsTests
{
    [Fact]
    public void 인자없음_기본값으로_성공한다()
    {
        Assert.True(LoadTestOptions.TryParse([], out var options, out var error));
        Assert.Null(error);
        Assert.NotNull(options);
        Assert.Equal("game", options!.Mode);
        Assert.Equal(100, options.Clients);
        Assert.Equal(200, options.RampUpPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Duration);
        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal(7777, options.GamePort);
        Assert.Equal(7778, options.AuthPort);
        Assert.Equal(7779, options.TelemetryPort);
        Assert.False(options.NoTelemetry);
        Assert.Equal(3000, options.Accounts);
        Assert.Equal(0.99, options.MinRetention);
        Assert.Equal(0.001, options.MaxErrorRate);
        Assert.Null(options.ServerPid);
        Assert.Null(options.ServerProcessName);
        Assert.Equal("logs", options.OutDirectory);
        Assert.Equal(256, options.MaxLogMb);
    }

    [Fact]
    public void 전체옵션_지정값으로_파싱된다()
    {
        string[] args =
        [
            "--mode", "full", "--clients", "10000", "--ramp-up", "500", "--duration", "72h",
            "--host", "10.0.0.5", "--game-port", "8001", "--auth-port", "8002", "--telemetry-port", "8003",
            "--no-telemetry", "--ping-interval", "2s", "--auth-timeout", "5s", "--report-interval", "30s",
            "--stall-timeout", "45s", "--login-concurrency", "16", "--reconnect-delay", "1s",
            "--accounts", "1500", "--token-ttl", "30m", "--rtt-p50-max", "50ms", "--rtt-p95-max", "150ms",
            "--rtt-p99-max", "300ms", "--min-retention", "0.95", "--max-error-rate", "0.01",
            "--server-pid", "1234", "--server-max-ws-mb", "2048", "--out", "results", "--max-log-mb", "64",
        ];

        Assert.True(LoadTestOptions.TryParse(args, out var options, out _));
        Assert.Equal("full", options!.Mode);
        Assert.Equal(10000, options.Clients);
        Assert.Equal(500, options.RampUpPerSecond);
        Assert.Equal(TimeSpan.FromHours(72), options.Duration);
        Assert.Equal("10.0.0.5", options.Host);
        Assert.Equal(8001, options.GamePort);
        Assert.True(options.NoTelemetry);
        Assert.Equal(TimeSpan.FromSeconds(2), options.PingInterval);
        Assert.Equal(TimeSpan.FromSeconds(45), options.StallTimeout);
        Assert.Equal(16, options.LoginConcurrency);
        Assert.Equal(1500, options.Accounts);
        Assert.Equal(TimeSpan.FromMinutes(30), options.TokenTtl);
        Assert.Equal(TimeSpan.FromMilliseconds(50), options.RttP50Max);
        Assert.Equal(0.95, options.MinRetention);
        Assert.Equal(1234, options.ServerPid);
        Assert.Equal(2048, options.ServerMaxWorkingSetMb);
        Assert.Equal("results", options.OutDirectory);
        Assert.Equal(64, options.MaxLogMb);
    }

    [Theory]
    [InlineData("72h", 72 * 3600_000)]
    [InlineData("30m", 30 * 60_000)]
    [InlineData("60s", 60_000)]
    [InlineData("500ms", 500)]
    [InlineData("1.5h", 90 * 60_000)]
    [InlineData("0.5s", 500)]
    public void 기간파서_유효형식_성공(string text, double expectedMs)
    {
        Assert.True(LoadTestOptions.TryParseDuration(text, out var value));
        Assert.Equal(expectedMs, value.TotalMilliseconds, precision: 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("60")]
    [InlineData("abc")]
    [InlineData("s")]
    [InlineData("-5s")]
    [InlineData("5d")]
    public void 기간파서_무효형식_실패(string text)
    {
        Assert.False(LoadTestOptions.TryParseDuration(text, out _));
    }

    [Theory]
    [InlineData("--mode", "banana")]
    [InlineData("--clients", "0")]
    [InlineData("--clients", "-5")]
    [InlineData("--game-port", "70000")]
    [InlineData("--min-retention", "1.5")]
    [InlineData("--duration", "sixty")]
    public void 무효값_오류메시지와_실패(string flag, string value)
    {
        Assert.False(LoadTestOptions.TryParse([flag, value], out var options, out var error));
        Assert.Null(options);
        Assert.NotNull(error);
    }

    [Fact]
    public void 알수없는옵션_실패()
    {
        Assert.False(LoadTestOptions.TryParse(["--bogus", "1"], out _, out var error));
        Assert.Contains("--bogus", error);
    }

    [Fact]
    public void 값누락_실패()
    {
        Assert.False(LoadTestOptions.TryParse(["--clients"], out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void 도움말_실패반환하되_오류는없다()
    {
        Assert.False(LoadTestOptions.TryParse(["--help"], out var options, out var error));
        Assert.Null(options);
        Assert.Null(error);
    }
}
