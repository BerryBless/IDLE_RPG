using System.Diagnostics;
using System.Globalization;
using System.Text;
using LoadTester.Metrics;
using LoadTester.Options;
using LoadTester.Telemetry;
using LoadTester.Verdict;

namespace LoadTester.Coordination;

/// <summary>
/// 멀티프로세스 용량 하네스의 코디네이터. K개 워커 프로세스를 스폰해 각각 클라이언트 샤드를
/// 담당시키고, 워커 stdout의 <c>@interval</c>/<c>@final</c> 라인을 합산해 통합 콘솔/NDJSON/판정을
/// 만든다. 서버 텔레메트리·리소스 샘플링은 코디네이터만 수행(K프로세스 중복 방지).
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> <see cref="RunAsync"/> 1회 호출 전용. 워커 stdout 콜백(멀티 스레드)은
/// <see cref="CombinedAggregator"/>(thread-safe)로만 기록한다. <b>[Blocking:]</b> Non-blocking(비동기).
/// </remarks>
public static class CoordinatorRunner
{
    /// <summary>코디네이터를 실행하고 종료 코드(0 PASS/1 FAIL/3 중단)를 반환합니다.</summary>
    public static async Task<int> RunAsync(LoadTestOptions options, CancellationToken lifetime)
    {
        int target = options.TargetConcurrent ?? options.Clients;
        int workers = options.Workers;
        string outRoot = options.OutDirectory;

        var telemetry = options.NoTelemetry ? null : new TelemetrySubscriber();
        var resources = new ResourceMonitor(options.ServerPid, options.ServerProcessName);
        var aggregator = new CombinedAggregator(workers, target, telemetry, resources);
        var verdict = new CapacityVerdictEvaluator(CapacityThresholds.FromOptions(options));
        using var combinedNdjson = new CombinedNdjsonWriter(outRoot);

        Console.WriteLine($"[코디네이터] workers={workers} port-count={options.PortCount} " +
                          $"target={target:N0} duration={options.Duration} → {options.Host}:{options.GamePort}" +
                          (options.PortCount > 1 ? $"..{options.GamePort + options.PortCount - 1}" : ""));
        combinedNdjson.WriteRunStart(workers, options.PortCount, target, options.Duration.TotalSeconds);

        // 서버 텔레메트리 구독 루프(코디네이터만).
        Task? telemetryTask = telemetry is not null
            ? telemetry.RunAsync(options.Host, options.TelemetryPort, lifetime)
            : null;

        // 워커 스폰 + stdout/stderr 파이프 배선.
        var processes = new List<Process>(workers);
        for (int i = 0; i < workers; i++)
        {
            (int count, int offset) = WorkerShard.ForWorker(options.Clients, workers, i);
            int workerIndex = i;
            Process p = WorkerProcessLauncher.Launch(options, workerIndex, count, offset, outRoot);
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                // @interval/@final이면 집계기가 흡수, 아니면 사람용 로그로 접두사 붙여 재출력.
                if (!aggregator.OfferLine(e.Data))
                    Console.WriteLine($"[w{workerIndex}] {e.Data}");
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                    Console.Error.WriteLine($"[w{workerIndex}!] {e.Data}");
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            processes.Add(p);
        }
        Console.WriteLine($"[코디네이터] 워커 {workers}개 스폰 완료. 집계 시작(주기 {options.ReportInterval.TotalSeconds:0}s).");

        // 전 워커 종료 대기 태스크. 워커는 --duration으로 자체 종료한다.
        var exitTasks = processes.Select(p => p.WaitForExitAsync(CancellationToken.None)).ToArray();
        Task allExited = Task.WhenAll(exitTasks);

        var elapsed = Stopwatch.StartNew();
        bool aborted = false;

        // 코디네이터 자체 데드라인: 워커는 --duration으로 자가 종료하지만, 워커 하나가 셧다운에서 멈추면
        // allExited가 영원히 완료되지 않아 코디네이터가 무한 대기한다. duration + 여유(30s)를 넘기면
        // 관측 루프를 강제 종료하고 아래에서 잔여 워커를 kill한다.
        TimeSpan coordinatorDeadline = options.Duration + TimeSpan.FromSeconds(30);

        // 집계 루프: 리포트 주기마다 통합 스냅샷을 만들어 콘솔·NDJSON·판정에 반영. 전 워커 종료·
        // 데드라인 초과·lifetime 취소 중 하나로 종료.
        using var timer = new PeriodicTimer(options.ReportInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime))
            {
                // 워커가 모두 종료했으면 이 틱은 셧다운 구간이라 관측에서 제외한다 — 종료 진행 중의
                // active 감소(→0)를 유지율/유실 판정에 넣으면 정상 종료가 대량 끊김으로 오판된다.
                if (allExited.IsCompleted)
                    break;
                if (elapsed.Elapsed > coordinatorDeadline)
                {
                    Console.WriteLine("[코디네이터] 데드라인 초과 — 잔여 워커를 강제 종료하고 마무리합니다.");
                    break;
                }

                CombinedInterval combined = aggregator.BuildCombined(elapsed.Elapsed.TotalSeconds);
                PrintCombined(combined);
                combinedNdjson.WriteInterval(combined);
                verdict.Observe(combined);
            }
        }
        catch (OperationCanceledException)
        {
            aborted = true; // Ctrl+C
        }

        // 종료 드레인: 워커가 @final을 마저 내보내고 종료할 시간을 준다(중단 시엔 짧게).
        try
        {
            await allExited.WaitAsync(aborted ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(20));
        }
        catch (TimeoutException)
        {
            Console.WriteLine("[코디네이터] 일부 워커가 제한 시간 내 종료되지 않아 강제 종료합니다.");
        }
        foreach (Process p in processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (Exception) { /* 이미 종료 */ }
        }

        // 종료 후 재관측하지 않는다: 워커가 이미 빠져나가 active가 0에 수렴하므로 관측하면 유지율이
        // 오염된다. 판정은 실행 중 관측한 클린 구간들로만 이뤄진다.

        // 워커 무결성: 전 워커가 interval≥1 보고 && @final 전달 && 종료코드 0/1/3.
        bool allHealthy = aggregator.ReportingWorkerCount == workers && aggregator.FinalCount == workers
                          && processes.All(p => p.HasExited && p.ExitCode is 0 or 1 or 3);

        CapacityStats stats = verdict.BuildStats(allHealthy, elapsed.Elapsed.TotalSeconds);
        Verdict.Verdict result = verdict.Evaluate(stats);
        combinedNdjson.WriteRunEnd(result, stats);

        PrintFinalReport(result, stats, aborted, workers, aggregator, processes, combinedNdjson.FilePath);

        if (aborted)
            return 3;
        return result.Passed ? 0 : 1;
    }

    private static void PrintCombined(CombinedInterval c)
    {
        var e = TimeSpan.FromSeconds(c.ElapsedSeconds);
        string srv = c.ServerWorkingSetMb is double ws
            ? FormattableString.Invariant($" | srv {ws:F0}MB {(c.ServerCpuPercent is double cpu ? cpu.ToString("F0", CultureInfo.InvariantCulture) : "-")}%")
            : c.ServerProcessLost ? " | srv LOST" : string.Empty;
        string tele = c.TeleConnected is int tc ? FormattableString.Invariant($" | tele {tc:N0}") : string.Empty;
        Console.WriteLine(FormattableString.Invariant(
            $"[+{(int)e.TotalHours:D2}:{e.Minutes:D2}:{e.Seconds:D2}] conn {c.Active:N0}/{c.Target:N0} auth {c.Authenticated:N0}")
            + FormattableString.Invariant($" | w {c.WorkersReporting}/{c.TotalWorkers}")
            + FormattableString.Invariant($" | err {c.TotalFailures:N0} disc {c.UnexpectedDisconnects:N0}")
            + FormattableString.Invariant($" | stall {c.StalledClients:N0} | wWS {c.MaxWorkerWorkingSetMb:F0}MB")
            + srv + tele);
    }

    private static void PrintFinalReport(Verdict.Verdict result, CapacityStats stats, bool aborted,
        int workers, CombinedAggregator aggregator, IReadOnlyList<Process> processes, string ndjsonPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("════════════════ 용량 테스트 최종 리포트 ════════════════");
        sb.AppendLine($"판정            : {(result.Passed ? "PASS" : "FAIL")}{(aborted ? " (사용자 중단)" : string.Empty)}");
        foreach (string reason in result.Reasons)
            sb.AppendLine($"  - {reason}");
        sb.AppendLine($"실행 시간       : {TimeSpan.FromSeconds(stats.ElapsedSeconds):hh\\:mm\\:ss}");
        sb.AppendLine($"최대 동시 인증  : {stats.PeakAuthenticated:N0}" +
                      (stats.PeakTeleConnected is int t ? $" (서버 텔레메트리 최대 {t:N0})" : string.Empty));
        sb.AppendLine($"램프 완료       : {(stats.RampCompleted ? "예" : "아니오")}");
        sb.AppendLine($"램프 후 유지율  : 평균 {stats.RetentionMeanAfterRamp:P2} · 최저 {stats.MinRetentionAfterRamp:P2}");
        sb.AppendLine($"오류            : 실패 {stats.TotalFailures:N0} / 시도 {stats.TotalConnectAttempts:N0}");
        if (stats.MaxServerWorkingSetMb is double maxWs)
            sb.AppendLine($"서버 워킹셋     : 최대 {maxWs:F0}MB{(stats.ServerProcessLostObserved ? " (프로세스 소실!)" : string.Empty)}");
        sb.AppendLine($"워커            : 보고 {aggregator.ReportingWorkerCount}/{workers} · @final {aggregator.FinalCount}/{workers} · " +
                      $"종료코드 [{string.Join(",", processes.Select(p => p.HasExited ? p.ExitCode.ToString(CultureInfo.InvariantCulture) : "?"))}]");
        sb.AppendLine($"통합 NDJSON     : {ndjsonPath}");
        sb.Append("══════════════════════════════════════════════════════");
        Console.WriteLine(sb.ToString());
    }
}
