using System.Globalization;
using LoadTester.Metrics;

namespace LoadTester.Output;

/// <summary>
/// 구간 리포트를 콘솔 1줄 요약으로 출력합니다. 예:
/// <c>[+02:14:30] conn 9998/10000 | rtt p50 3.1 p95 11.0 p99 38.0ms | bcast 84.2k/s | err c0 a0 l0 d2 | stall 0 | srv 812MB 34% | tele 9998</c>
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe(Console.WriteLine 자체가 동기화됨) — 다만 설계상 샘플러
/// 스레드만 호출한다. <b>[Blocking:]</b> 콘솔 버퍼 동기 쓰기(수 µs). Windows QuickEdit 모드에서
/// 텍스트 선택 시 쓰기가 정지될 수 있으므로 장기 실행 시 QuickEdit 비활성화를 권장한다.
/// </remarks>
public sealed class ConsoleReporter : IIntervalReporter
{
    private readonly TimeSpan _reportInterval;

    /// <summary>리포터를 생성합니다.</summary>
    /// <param name="reportInterval">브로드캐스트 초당 환산에 쓸 구간 길이.</param>
    public ConsoleReporter(TimeSpan reportInterval)
    {
        _reportInterval = reportInterval;
    }

    /// <summary>구간 리포트 1줄을 포맷합니다(순수 함수, 테스트 대상).</summary>
    public string Format(IntervalReport r)
    {
        var elapsed = TimeSpan.FromSeconds(r.ElapsedSeconds);
        double bcastPerSec = _reportInterval.TotalSeconds > 0
            ? r.BroadcastsDelta / _reportInterval.TotalSeconds
            : 0;

        string srv;
        if (r.Resource.ServerWorkingSetMb is double ws)
        {
            string cpuText = r.Resource.ServerCpuPercent is double cpu
                ? cpu.ToString("F0", CultureInfo.InvariantCulture)
                : "-";
            srv = FormattableString.Invariant($" | srv {ws:F0}MB {cpuText}%");
        }
        else
        {
            srv = r.Resource.ServerProcessLost ? " | srv LOST" : string.Empty;
        }

        string tele = r.TeleConnected is int tc
            ? FormattableString.Invariant($" | tele {tc}")
            : string.Empty;

        return FormattableString.Invariant($"[+{(int)elapsed.TotalHours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}] conn {r.Active}/{r.Target} auth {r.Authenticated}")
            + FormattableString.Invariant($" | rtt p50 {r.RttP50Ms:F1} p95 {r.RttP95Ms:F1} p99 {r.RttP99Ms:F1}ms")
            + FormattableString.Invariant($" | bcast {FormatRate(bcastPerSec)}/s")
            + FormattableString.Invariant($" | err c{r.Totals.ConnectFailures} a{r.Totals.AuthFailures + r.Totals.AuthTimeouts} l{r.Totals.LoginFailures} d{r.Totals.UnexpectedDisconnects}")
            + FormattableString.Invariant($" | stall {r.StalledClients}{(r.FullStall ? "!" : string.Empty)}")
            + srv + tele;
    }

    /// <summary>구간 리포트를 콘솔에 출력합니다.</summary>
    public void Report(IntervalReport report) => Console.WriteLine(Format(report));

    private static string FormatRate(double perSec) => perSec switch
    {
        >= 1_000_000 => FormattableString.Invariant($"{perSec / 1_000_000:F1}M"),
        >= 1_000 => FormattableString.Invariant($"{perSec / 1_000:F1}k"),
        _ => perSec.ToString("F0", CultureInfo.InvariantCulture),
    };
}
