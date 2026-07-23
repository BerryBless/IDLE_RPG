using LoadTester.Metrics;
using LoadTester.Output;
using LoadTester.Verdict;

namespace LoadTester.Coordination;

/// <summary>
/// 워커 역할에서 <see cref="ConsoleReporter"/> 대신 쓰이는 리포터. 구간 리포트를 <c>@interval {json}</c>
/// 라인으로 stdout에 출력해 부모(코디네이터)가 <see cref="System.Diagnostics.Process.OutputDataReceived"/>로
/// 무폴링 수신하게 합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> 샘플러 스레드 단일 호출 전제(Console.WriteLine 자체는 동기화됨).
/// <b>[Blocking:]</b> stdout 동기 쓰기. <b>[Memory:]</b> 라인당 JSON 문자열 1개(리포트 주기 — 무해).
/// </remarks>
public sealed class StatusLineEmitter : IIntervalReporter
{
    private readonly int _workerIndex;

    /// <summary>워커 인덱스로 이미터를 생성합니다.</summary>
    public StatusLineEmitter(int workerIndex)
    {
        _workerIndex = workerIndex;
    }

    /// <inheritdoc/>
    public void Report(IntervalReport report)
    {
        var status = new WorkerStatus(
            WorkerIndex: _workerIndex,
            ElapsedSec: report.ElapsedSeconds,
            Active: report.Active,
            Target: report.Target,
            Authenticated: report.Authenticated,
            ConnectAttempts: report.Totals.ConnectAttempts,
            TotalFailures: report.Totals.TotalFailures,
            UnexpectedDisconnects: report.Totals.UnexpectedDisconnects,
            Reconnects: report.Totals.Reconnects,
            StalledClients: report.StalledClients,
            SelfWorkingSetMb: report.Resource.SelfWorkingSetMb);
        Console.WriteLine(WorkerLineProtocol.FormatInterval(status));
    }

    /// <summary>워커 종료 시 최종 요약을 <c>@final</c> 라인으로 출력합니다.</summary>
    /// <param name="stats">워커 로컬 최종 통계.</param>
    /// <param name="verdict">워커 로컬 판정(참고용).</param>
    /// <param name="peakAuthenticated">워커가 관측한 최대 동시 인증 수.</param>
    /// <param name="exitReason">종료 사유(normal|aborted).</param>
    public void EmitFinal(FinalStats stats, Verdict.Verdict verdict, int peakAuthenticated, string exitReason)
    {
        var final = new WorkerFinal(
            WorkerIndex: _workerIndex,
            Passed: verdict.Passed,
            PeakAuthenticated: peakAuthenticated,
            ConnectAttempts: stats.Totals.ConnectAttempts,
            TotalFailures: stats.Totals.TotalFailures,
            ExitReason: exitReason);
        Console.WriteLine(WorkerLineProtocol.FormatFinal(final));
    }
}
