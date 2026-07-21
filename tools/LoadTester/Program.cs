using System.Text;
using LoadTester.Auth;
using LoadTester.Client;
using LoadTester.Coordination;
using LoadTester.Metrics;
using LoadTester.Options;
using LoadTester.Output;
using LoadTester.Stress;
using LoadTester.Telemetry;
using LoadTester.Verdict;
using ServerLib.Core.Auth;

// ─────────────────────────────────────────────────────────────────────────────
// LoadTester — GameServer/AuthServer 성능 측정·부하 테스트 콘솔 툴
//   game 모드: HMAC 토큰 직접 발급 → GameServer(7777)만 부하 (MongoDB 불필요)
//   full 모드: AuthServer(7778) 로그인 → 토큰 획득 → GameServer 부하
// 종료 코드: 0 PASS · 1 FAIL · 2 사용법/구성 오류 · 3 지속시간 전 중단
// ─────────────────────────────────────────────────────────────────────────────

if (!LoadTestOptions.TryParse(args, out LoadTestOptions? options, out string? parseError))
{
    if (parseError is not null)
    {
        Console.Error.WriteLine($"[오류] {parseError}");
        Console.Error.WriteLine();
    }
    Console.WriteLine(LoadTestOptions.UsageText);
    return parseError is null ? 0 : 2;
}

// 스트레스 시나리오 지정 시: 페이즈 기반 StressRunner로 분기(코디네이터/워커 분기 전).
// 단, 스트레스가 스폰한 버스트/churn 워커는 여전히 --role worker 경로를 타야 하므로 worker면 제외.
if (options!.Stress is not null && options.Role != "worker")
{
    using var stressCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stressCts.Cancel(); };
    return await StressRunner.RunAsync(options, stressCts.Token);
}

// 역할 결정: coordinator면 워커 K개를 스폰·집계하고 여기서 끝낸다. 단일 프로세스당 포트 경고는
// 워커 샤드(포트 분산 고려) 기준이므로 코디네이터/워커 분기 이후에 판단한다.
bool isCoordinator = options.Role == "coordinator" || (options.Role == "auto" && options.Workers > 1);
if (isCoordinator)
{
    using var coordCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; coordCts.Cancel(); };
    return await CoordinatorRunner.RunAsync(options, coordCts.Token);
}

bool isWorker = options.Role == "worker";

// 단일 포트당 클라이언트 수가 임시 포트 상한에 근접하면 경고. 멀티포트면 포트당 부하가 나뉘므로
// 유효 상한은 clients/PortCount 기준이다.
int clientsPerPort = options.Clients / Math.Max(1, options.PortCount);
if (clientsPerPort > LoadTestOptions.ClientsPortWarningThreshold)
{
    // Windows 동적 포트 기본 범위는 ~16,384개. capacity-tune.bat(netsh)로 확장하고 --port-count를
    // 늘려 포트당 부하를 낮추면 상한이 포트 수배로 넓어진다.
    Console.WriteLine($"[경고] 포트당 클라이언트 {clientsPerPort}(= {options.Clients}/{options.PortCount}) > " +
                      $"{LoadTestOptions.ClientsPortWarningThreshold}: 임시 포트 고갈 위험. " +
                      "capacity-tune.bat로 동적 포트 확장 + --port-count 증대를 권장합니다.");
}

// 토큰 소스 구성(모드 분기).
var credentials = new CredentialProvider(options.Accounts);
ITokenSource tokenSource;
if (options.Mode == "game")
{
    byte[]? secret = ResolveHmacSecret();
    if (secret is null)
        return 2;
    tokenSource = new LocalHmacTokenSource(new HmacAuthTokenCodec(secret), credentials, options.TokenTtl);
}
else
{
    tokenSource = new AuthServerTokenSource(
        options.Host, options.AuthPort, credentials, options.LoginConcurrency, options.TokenTtl);
}

var metrics = new MetricsAggregator();
var controller = new LoadController(options, tokenSource, metrics);
// 워커는 텔레메트리·서버 리소스 샘플링을 하지 않는다(코디네이터만 소유 — K프로세스 중복 방지).
var telemetry = (options.NoTelemetry || isWorker) ? null : new TelemetrySubscriber();
var resources = new ResourceMonitor(options.ServerPid, options.ServerProcessName);
// 워커면 StatusLineEmitter(@interval stdout), 아니면 ConsoleReporter(사람용 콘솔 1줄).
var statusEmitter = isWorker ? new StatusLineEmitter(options.WorkerIndex) : null;
IIntervalReporter reporter = statusEmitter ?? (IIntervalReporter)new ConsoleReporter(options.ReportInterval);
var verdictEvaluator = new VerdictEvaluator(VerdictThresholds.FromOptions(options));
using var ndjson = new NdjsonMetricsWriter(options.OutDirectory, options.MaxLogMb);
var sampler = new MetricsSampler(
    options, controller.Clients, metrics, telemetry, resources, reporter, ndjson, verdictEvaluator);

// 수명 관리: 지속시간 경과(정상) ∪ Ctrl+C(중단). 어느 쪽이든 같은 토큰으로 전 컴포넌트를 세운다.
using var lifetimeCts = new CancellationTokenSource();
bool userAborted = false;
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // 프로세스 즉사 대신 협조적 셧다운 → 최종 리포트 보장
    userAborted = true;
    lifetimeCts.Cancel();
};
lifetimeCts.CancelAfter(options.Duration);

Console.WriteLine($"[시작] mode={options.Mode} clients={options.Clients} ramp-up={options.RampUpPerSecond}/s " +
                  $"duration={options.Duration} → {options.Host}:{options.GamePort}");
ndjson.WriteRunStart(options);

var runTasks = new List<Task>
{
    controller.RunAsync(lifetimeCts.Token),
    sampler.RunAsync(lifetimeCts.Token),
};
if (telemetry is not null)
    runTasks.Add(telemetry.RunAsync(options.Host, options.TelemetryPort, lifetimeCts.Token));

// 실행: 전 태스크는 lifetime 취소 시 자체적으로 정상 반환한다(내부에서 OCE 흡수).
Task allTasks = Task.WhenAll(runTasks);
try
{
    await allTasks.WaitAsync(lifetimeCts.Token); // 전 태스크 완료 또는 수명 만료 중 먼저 오는 쪽
}
catch (OperationCanceledException)
{
    // 셧다운 드레인: 취소 후 클라이언트 태스크들이 소켓 정리를 마칠 시간을 15초로 상한한다
    // (일부 태스크가 걸려도 최종 리포트는 반드시 출력).
    try
    {
        await allTasks.WaitAsync(TimeSpan.FromSeconds(15));
    }
    catch (TimeoutException)
    {
        Console.WriteLine("[경고] 일부 태스크가 15초 드레인 내에 종료되지 않았습니다 — 최종 리포트를 강행합니다.");
    }
    catch (Exception)
    {
        // 드레인 중 태스크 예외도 최종 리포트를 막지 않는다.
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[오류] 실행 태스크 비정상 종료: {ex.Message}");
}

// 최종 판정 + runEnd 기록.
FinalStats finalStats = sampler.BuildFinalStats();
Verdict verdict = verdictEvaluator.Evaluate(finalStats);
ndjson.WriteRunEnd(verdict, finalStats);

// 워커: 사람용 요약 대신 @final 라인 1개만 stdout으로 내보내고 종료(코디네이터가 파싱).
if (statusEmitter is not null)
{
    statusEmitter.EmitFinal(finalStats, verdict, sampler.PeakAuthenticated, userAborted ? "aborted" : "normal");
    return userAborted ? 3 : (verdict.Passed ? 0 : 1);
}

var summary = new StringBuilder();
summary.AppendLine();
summary.AppendLine("──────────────── 최종 리포트 ────────────────");
summary.AppendLine($"판정        : {(verdict.Passed ? "PASS" : "FAIL")}{(userAborted ? " (사용자 중단)" : string.Empty)}");
foreach (string reason in verdict.Reasons)
    summary.AppendLine($"  - {reason}");
summary.AppendLine($"실행 시간   : {TimeSpan.FromSeconds(finalStats.ElapsedSeconds):hh\\:mm\\:ss}");
summary.AppendLine($"RTT (누적)  : p50 {finalStats.CumRttP50Ms:F1}ms · p95 {finalStats.CumRttP95Ms:F1}ms · p99 {finalStats.CumRttP99Ms:F1}ms");
summary.AppendLine($"연결        : 시도 {finalStats.Totals.ConnectAttempts} · 실패 {finalStats.Totals.ConnectFailures} · " +
                   $"예기치 않은 끊김 {finalStats.Totals.UnexpectedDisconnects} · 재접속 {finalStats.Totals.Reconnects}");
summary.AppendLine($"인증        : 성공 {finalStats.Totals.AuthSuccesses} · 거부 {finalStats.Totals.AuthFailures} · " +
                   $"타임아웃 {finalStats.Totals.AuthTimeouts} · 로그인 실패 {finalStats.Totals.LoginFailures}");
summary.AppendLine($"수신        : 브로드캐스트 {finalStats.Totals.Broadcasts:N0} · {finalStats.Totals.BytesIn:N0} bytes");
summary.AppendLine($"유지율 평균 : {finalStats.RetentionMean:P2} · 전면 스톨 {finalStats.StallIncidents}회");
if (finalStats.MaxServerWorkingSetMb is double maxWs)
    summary.AppendLine($"서버 워킹셋 : 최대 {maxWs:F0}MB{(finalStats.ServerProcessLostObserved ? " (프로세스 소실 관측!)" : string.Empty)}");
summary.AppendLine($"NDJSON      : {ndjson.CurrentFilePath}");
summary.Append("─────────────────────────────────────────────");
Console.WriteLine(summary.ToString());

if (userAborted)
    return 3;
return verdict.Passed ? 0 : 1;

// GameServer/Main.cs의 ResolveHmacSecret과 동일 정책 미러링: env 우선, DEBUG 빌드만 개발용 폴백.
// 대상 GameServer가 로드한 비밀키와 일치해야 토큰이 검증을 통과한다.
static byte[]? ResolveHmacSecret()
{
    string? fromEnv = Environment.GetEnvironmentVariable("IDLERPG_AUTH_HMAC_SECRET");
    string secretText;
    if (fromEnv is not null)
    {
        secretText = fromEnv;
    }
    else
    {
#if DEBUG
        Console.WriteLine("[경고] IDLERPG_AUTH_HMAC_SECRET 환경 변수가 없어 개발용 기본 비밀키를 사용합니다. " +
                          "대상 GameServer(DEBUG 빌드)도 동일 폴백을 써야 인증이 통과됩니다.");
        secretText = "dev-only-insecure-hmac-secret-change-me";
#else
        Console.Error.WriteLine("[오류] IDLERPG_AUTH_HMAC_SECRET 환경 변수가 설정되지 않았습니다. " +
                                "Release 빌드에서는 개발용 기본 비밀키를 사용하지 않습니다.");
        return null;
#endif
    }
    return Encoding.UTF8.GetBytes(secretText);
}
