using System.Diagnostics;
using System.Globalization;
using System.Text;
using LoadTester.Auth;
using LoadTester.Metrics;
using LoadTester.Options;
using LoadTester.Stress.Scenarios;
using LoadTester.Telemetry;
using ServerLib.Core.Auth;

namespace LoadTester.Stress;

/// <summary>
/// 스트레스 테스트 오케스트레이터. 정상 대조군 프로브를 전 구간 구동하면서 Baseline→During→Release→
/// Recovery 페이즈를 순차 진행하고, 각 구간을 샘플링해 콘솔·NDJSON·판정에 반영합니다. 서버는 무변경
/// (측정 전용) — 이 러너는 서버 밖에서 관측만 합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> <see cref="RunAsync"/> 1회 호출 전용. 샘플러 루프가 프로브·시나리오 스냅샷의
/// 유일한 리더. <b>[Blocking:]</b> Non-blocking(비동기).
/// </remarks>
public static class StressRunner
{
    /// <summary>스트레스 시나리오를 실행하고 종료 코드(0 PASS/1 FAIL/2 구성/3 중단)를 반환합니다.</summary>
    public static async Task<int> RunAsync(LoadTestOptions options, CancellationToken lifetime)
    {
        StressScenarioKind kind = options.Stress!.Value;

        byte[]? secret = ResolveHmacSecret();
        if (secret is null)
            return 2;
        var tokens = new LocalHmacTokenSource(new HmacAuthTokenCodec(secret),
            new CredentialProvider(options.Accounts), options.TokenTtl);

        var telemetry = options.NoTelemetry ? null : new TelemetrySubscriber();
        var resources = new ResourceMonitor(options.ServerPid, options.ServerProcessName);
        var probe = new ControlProbe(options, tokens);
        var context = new StressRunContext
        {
            Options = options, TokenSource = tokens, Telemetry = telemetry,
            Resources = resources, OutDirectory = options.OutDirectory,
        };
        IStressScenario scenario = CreateScenario(kind, options);
        var clock = new StressPhaseClock(options.BaselineDuration, options.StressDuration, options.RecoveryDuration);
        var verdict = new StressVerdictEvaluator(new StressThresholds(), scenario.Expectations, clock.TotalSeconds);
        using var ndjson = new StressNdjsonWriter(options.OutDirectory, kind);

        Console.WriteLine($"[스트레스] scenario={kind} probe={options.ProbeClients} " +
                          $"baseline={options.BaselineDuration}/during={options.StressDuration}/recovery={options.RecoveryDuration} " +
                          $"→ {options.Host}:{options.GamePort}");
        Console.WriteLine($"  기대 발견: {scenario.Expectations.HeadlineFinding}");
        ndjson.WriteRunStart(options);

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        using var stressCts = CancellationTokenSource.CreateLinkedTokenSource(runCts.Token);

        // 프로브·텔레메트리는 전 구간 병행.
        Task probeTask = probe.RunAsync(runCts.Token);
        Task? telemetryTask = telemetry?.RunAsync(options.Host, options.TelemetryPort, runCts.Token);

        var rttScratch = new RttHistogram();
        var elapsed = Stopwatch.StartNew();
        bool aborted = false;
        StressPhase prevPhase = StressPhase.Baseline;
        StressDriverSnapshot lastDriver = default;
        Task? releaseTask = null;

        // 단일 샘플러 루프(2s): 페이즈 경계에서 시나리오 시작/해제를 트리거하고 매 틱 관측한다.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(runCts.Token))
            {
                double now = elapsed.Elapsed.TotalSeconds;
                StressPhase phase = clock.PhaseAt(now);

                // 페이즈 전이 부수효과.
                if (phase != prevPhase)
                {
                    if (phase == StressPhase.During)
                    {
                        await scenario.DriveAsync(context, stressCts.Token);
                        Console.WriteLine($"[스트레스] During 시작 — 시나리오 구동");
                    }
                    else if (phase == StressPhase.Recovery)
                    {
                        // Release: 스트레스 취소 후 정리(워커 kill 등). 회복 측정 전에 완료해야 한다.
                        stressCts.Cancel();
                        Console.WriteLine($"[스트레스] Release — 스트레스 해제, 회복 관측 시작");
                        releaseTask = scenario.ReleaseAsync();
                        await releaseTask;
                    }
                    prevPhase = phase;
                }

                lastDriver = scenario.Snapshot();
                ProbeHealthSnapshot probeSnap = probe.Sample(rttScratch);
                TelemetrySample? tele = telemetry?.Latest;
                ResourceSample res = resources.Sample();

                var report = new StressIntervalReport(phase, now, probeSnap, lastDriver,
                    tele?.ConnectedCount, tele?.RejectedConnections, res);
                PrintInterval(report);
                ndjson.WriteInterval(report);
                verdict.Observe(report);

                if (now >= clock.TotalSeconds)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            aborted = true;
        }

        // 시나리오가 아직 살아있으면(중단 등) 정리.
        if (releaseTask is null)
        {
            stressCts.Cancel();
            try { await scenario.ReleaseAsync(); } catch (Exception) { }
        }
        runCts.Cancel();
        try { await Task.WhenAll(new[] { probeTask, telemetryTask }.Where(t => t is not null)!).WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception) { }

        StressStats stats = verdict.BuildStats(kind, lastDriver, elapsed.Elapsed.TotalSeconds);
        Verdict.Verdict result = verdict.Evaluate(stats);
        ndjson.WriteRunEnd(result, stats, scenario.Expectations.HeadlineFinding);
        PrintFinalReport(kind, result, stats, scenario.Expectations, aborted, ndjson.FilePath);

        if (aborted) return 3;
        return result.Passed ? 0 : 1;
    }

    private static IStressScenario CreateScenario(StressScenarioKind kind, LoadTestOptions options) => kind switch
    {
        StressScenarioKind.Malformed => new MalformedScenario(options.StressClients, options.Host, options.GamePort),
        StressScenarioKind.Slowloris => new SlowlorisScenario(options.StressClients, options.Host, options.GamePort,
            drip: options.SlowlorisMode == "drip"),
        StressScenarioKind.Burst => new BurstScenario(options, options.OutDirectory),
        StressScenarioKind.Churn => new ChurnScenario(options, options.OutDirectory),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "알 수 없는 스트레스 시나리오"),
    };

    private static void PrintInterval(StressIntervalReport r)
    {
        string srv = r.Resource.ServerWorkingSetMb is double ws
            ? FormattableString.Invariant($" | srv {ws:F0}MB")
            : r.Resource.ServerProcessLost ? " | srv LOST" : string.Empty;
        string tele = r.TeleConnected is int tc ? FormattableString.Invariant($" | tele {tc:N0}") : string.Empty;
        string driver = r.Driver.MalformedFramesSent > 0
            ? FormattableString.Invariant($" | frames {r.Driver.MalformedFramesSent:N0}")
            : r.Driver.StressActive > 0 ? FormattableString.Invariant($" | stress {r.Driver.StressActive:N0}") : string.Empty;
        var e = TimeSpan.FromSeconds(r.ElapsedSeconds);
        Console.WriteLine(FormattableString.Invariant(
            $"[{r.Phase,-8}][+{(int)e.TotalMinutes:D2}:{e.Seconds:D2}] probe {r.Probe.Connected}/{r.Probe.Size} auth {r.Probe.Authenticated} rtt p95 {r.Probe.RttP95Ms:F1}ms")
            + driver + tele + srv);
    }

    private static void PrintFinalReport(StressScenarioKind kind, Verdict.Verdict result, StressStats s,
        StressExpectations exp, bool aborted, string ndjsonPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("════════════════ 스트레스 테스트 최종 리포트 ════════════════");
        sb.AppendLine($"시나리오        : {kind}");
        sb.AppendLine($"판정            : {(result.Passed ? "PASS" : "FAIL")}{(aborted ? " (사용자 중단)" : string.Empty)}");
        foreach (string reason in result.Reasons)
            sb.AppendLine($"  - {reason}");
        sb.AppendLine($"핵심 발견       : {exp.HeadlineFinding}");
        sb.AppendLine($"서버 생존       : {(s.ServerAliveAtEnd && !s.CrashObserved ? "예(크래시 없음)" : "아니오/크래시")}");
        sb.AppendLine($"프로브(과부하중): 연결 최저 {s.ProbeMinConnectedRatioDuring:P1} · 인증 최저 {s.ProbeMinAuthedRatioDuring:P1} · RTT p95 {s.ProbeRttP95During:F1}ms");
        sb.AppendLine($"기준선          : RTT p95 {s.Baseline.ProbeRttP95Ms:F1}ms · 서버접속 {s.Baseline.ServerConnected:N0} · WS {s.Baseline.ServerWsMb:F0}MB");
        sb.AppendLine($"피크            : 서버접속 {s.PeakServerConnected:N0} · WS {s.PeakServerWsMb:F0}MB");
        if (exp.ExpectSessionCountRecovery)
            sb.AppendLine($"회복            : {(s.SessionCountRecovered ? $"예(+{s.TimeToRecoverSeconds:F0}s)" : "아니오(기준선 미복귀)")}");
        else
            sb.AppendLine($"누적(리포트)    : 회복={(s.SessionCountRecovered ? "예" : "아니오(설계상 누적)")} · 정체 피크 {s.StalledHeldPeak:N0}" +
                          (s.WsGrowthPerStalledPeerKb > 0 ? $" · 피어당 WS +{s.WsGrowthPerStalledPeerKb:F1}KB" : ""));
        if (s.MalformedFramesSent > 0)
            sb.AppendLine($"악성 프레임      : {s.MalformedFramesSent:N0}건 전송, 서버 생존");
        if (s.StressConnectFailures > 0)
            sb.AppendLine($"스트레스 실패    : {s.StressConnectFailures:N0}");
        sb.AppendLine($"NDJSON          : {ndjsonPath}");
        sb.Append("══════════════════════════════════════════════════════");
        Console.WriteLine(sb.ToString());
    }

    // 해석 정책은 ServerLib 공용 HmacSecretResolver로 GameServer·AuthServer·Program.cs와 단일 소스를 공유한다
    // (이전 4중 복제 통합 — 코드리뷰 Medium). 32바이트 검증도 리졸버가 담당.
    private static byte[]? ResolveHmacSecret()
    {
        if (!HmacSecretResolver.TryResolve(out byte[] secret, out var source, out string? error))
        {
            Console.Error.WriteLine($"[오류] {error}");
            return null;
        }
        if (source == HmacSecretResolver.SecretSource.DevFallback)
            Console.WriteLine("[경고] IDLERPG_AUTH_HMAC_SECRET 없어 개발용 기본 비밀키 사용(대상 서버도 DEBUG 폴백이어야 함).");
        return secret;
    }
}
