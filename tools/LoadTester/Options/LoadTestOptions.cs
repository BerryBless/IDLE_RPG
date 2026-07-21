using System.Globalization;
using LoadTester.Stress;

namespace LoadTester.Options;

/// <summary>
/// LoadTester 실행 옵션 집합입니다. CLI 인자를 <see cref="TryParse"/>로 파싱해 생성하며,
/// 이후 전 컴포넌트가 읽기 전용으로 공유합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 불변 record — 파싱 이후 모든 스레드가
/// 동기화 없이 읽습니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 파싱 시 1회 할당. 이후 무할당.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 순수 문자열 파싱만 수행합니다.</description></item>
/// </list>
/// </remarks>
public sealed record LoadTestOptions
{
    /// <summary>부하 경로 모드. <c>"game"</c>=HMAC 토큰 직접 발급 후 GameServer만,
    /// <c>"full"</c>=AuthServer 로그인으로 토큰 획득 후 GameServer.</summary>
    public string Mode { get; init; } = "game";

    /// <summary>동시 가상 클라이언트 수.</summary>
    public int Clients { get; init; } = 100;

    /// <summary>초당 신규 연결 시도 상한(램프업 속도). 재접속에도 동일하게 적용된다.</summary>
    public int RampUpPerSecond { get; init; } = 200;

    /// <summary>총 실행 시간. 경과 시 정상 종료·판정한다.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>대상 서버 호스트.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>GameServer 게임 포트.</summary>
    public int GamePort { get; init; } = 7777;

    /// <summary>AuthServer 로그인 포트(full 모드 전용).</summary>
    public int AuthPort { get; init; } = 7778;

    /// <summary>GameServer 텔레메트리 포트(서버측 교차 검증용 구독).</summary>
    public int TelemetryPort { get; init; } = 7779;

    /// <summary>텔레메트리 구독 비활성화 여부.</summary>
    public bool NoTelemetry { get; init; }

    /// <summary>클라이언트별 자동 PING 주기(<c>IClientConnection.PingInterval</c>로 전달 → RTT 갱신 주기).</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>AuthTokenPacket 송신 후 ACK 대기 타임아웃.</summary>
    public TimeSpan AuthTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>콘솔/NDJSON 리포트 주기(샘플러 루프 주기).</summary>
    public TimeSpan ReportInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>클라이언트별 마지막 앱 패킷 수신 이후 이 시간이 지나면 스톨로 간주.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>full 모드 동시 로그인 상한(AuthServer PBKDF2 부하·포트 고갈 보호).</summary>
    public int LoginConcurrency { get; init; } = 32;

    /// <summary>재접속 백오프 기본 지연(지수 증가의 밑, cap 60s).</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>재사용할 시딩 계정 수(clientIndex % Accounts로 매핑).</summary>
    public int Accounts { get; init; } = 3000;

    /// <summary>game 모드에서 직접 발급하는 토큰의 유효기간.</summary>
    public TimeSpan TokenTtl { get; init; } = TimeSpan.FromHours(1);

    /// <summary>누적 RTT p50 판정 상한.</summary>
    public TimeSpan RttP50Max { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>누적 RTT p95 판정 상한.</summary>
    public TimeSpan RttP95Max { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>누적 RTT p99 판정 상한.</summary>
    public TimeSpan RttP99Max { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>구간 평균 연결 유지율(active/target) 판정 하한.</summary>
    public double MinRetention { get; init; } = 0.99;

    /// <summary>오류율((connectFail+authFail+authTimeout+loginFail)/시도) 판정 상한.</summary>
    public double MaxErrorRate { get; init; } = 0.001;

    /// <summary>리소스 모니터링 대상 서버 프로세스 PID(선택).</summary>
    public int? ServerPid { get; init; }

    /// <summary>리소스 모니터링 대상 서버 프로세스 이름(선택, PID 우선).</summary>
    public string? ServerProcessName { get; init; }

    /// <summary>서버 워킹셋 판정 상한 MB(지정 시 리소스 판정 규칙 활성화).</summary>
    public int? ServerMaxWorkingSetMb { get; init; }

    /// <summary>NDJSON 출력 디렉터리.</summary>
    public string OutDirectory { get; init; } = "logs";

    /// <summary>NDJSON 파일 로테이션 크기 상한(MB). 시간 경계(정시)에도 로테이션한다.</summary>
    public int MaxLogMb { get; init; } = 256;

    // ── 멀티프로세스·멀티포트 용량 하네스 옵션 (plan/capacity_harness_0721.md) ──

    /// <summary>스폰할 워커 프로세스 수. 2 이상이면 이 프로세스는 코디네이터가 된다(Role=auto 기준).</summary>
    public int Workers { get; init; } = 1;

    /// <summary>실행 역할. auto(기본)=Workers>1이면 코디네이터·아니면 단일, coordinator, worker.</summary>
    public string Role { get; init; } = "auto";

    /// <summary>워커 인덱스(0 이상 Workers 미만). 코디네이터가 각 워커에 주입한다.</summary>
    public int WorkerIndex { get; init; }

    /// <summary>서버가 연 게임 포트 수. 클라이언트는 <c>GamePort + globalIndex % PortCount</c>로 분산 접속한다.</summary>
    public int PortCount { get; init; } = 1;

    /// <summary>이 워커 클라이언트들의 전역 인덱스 시작 오프셋(계정·포트 매핑의 전역 유일성 확보).</summary>
    public int ClientIndexOffset { get; init; }

    /// <summary>용량 판정 모드. 활성 시 5규칙 대신 CapacityVerdictEvaluator를 쓴다.</summary>
    public bool Capacity { get; init; }

    /// <summary>용량 판정의 목표 동시 연결 수. 미지정 시 Clients(코디네이터의 총 클라이언트)를 쓴다.</summary>
    public int? TargetConcurrent { get; init; }

    /// <summary>
    /// 멀티포트(PortCount>1)에서 클라이언트가 명시적으로 바인드할 소스 포트의 시작값. 소스 포트를
    /// P개 목적지 포트에 재사용(SO_REUSEADDR)해 단일 소스 IP 임시 포트 상한을 P배로 넓힌다.
    /// OS 자동 임시 포트 범위(~1024..15000)·서버 리스너 포트·Windows 예약 범위와 겹치지 않게 25000 기본.
    /// </summary>
    public int SourcePortBase { get; init; } = 25000;

    // ── 스트레스 테스트 하네스 옵션 (plan/stress_harness_0721.md) ──

    /// <summary>스트레스 시나리오. null이면 일반 부하/용량 경로(스트레스 아님).</summary>
    public StressScenarioKind? Stress { get; init; }

    /// <summary>정상 대조군 프로브 클라이언트 수(connect+auth+hold, 전 페이즈 유지).</summary>
    public int ProbeClients { get; init; } = 200;

    /// <summary>인프로세스 스트레스 풀 크기(malformed/slowloris 적대적 클라이언트 수).</summary>
    public int StressClients { get; init; } = 4000;

    /// <summary>버스트/churn 과부하 목표 동시 연결 수(미지정 시 Clients).</summary>
    public int? StressTarget { get; init; }

    /// <summary>Baseline 페이즈 길이(프로브만으로 기준선 측정).</summary>
    public TimeSpan BaselineDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>During 페이즈 길이(스트레스 구동).</summary>
    public TimeSpan StressDuration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Recovery 페이즈 최대 길이(스트레스 해제 후 회복 관측).</summary>
    public TimeSpan RecoveryDuration { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>프로브 클라이언트 PING 주기(짧게 잡아 페이즈 내 RTT 곡선 해상도 확보).</summary>
    public TimeSpan ProbePingInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>slowloris 모드. silent=무송신, drip=1B/s 부분 프레임 드립.</summary>
    public string SlowlorisMode { get; init; } = "silent";

    /// <summary>churn 모드(내부): 인증 성공 후 즉시 종료·재접속 반복. 코디네이터가 churn 워커에 주입.</summary>
    public bool Churn { get; init; }

    /// <summary>이 수를 넘는 <see cref="Clients"/> 지정 시 Windows 동적 포트 고갈 경고를 출력한다.</summary>
    public const int ClientsPortWarningThreshold = 12_000;

    /// <summary>도움말 텍스트.</summary>
    public static string UsageText => """
        사용법: LoadTester --mode <full|game> [옵션]
          --mode              full|game                (기본 game)
          --clients           int                      (기본 100)
          --ramp-up           초당 연결 수             (기본 200)
          --duration          72h|30m|60s|500ms        (기본 60s)
          --host              string                   (기본 127.0.0.1)
          --game-port         int                      (기본 7777)
          --auth-port         int                      (기본 7778)   [full 모드]
          --telemetry-port    int                      (기본 7779)
          --no-telemetry                               (텔레메트리 구독 끔)
          --ping-interval     duration                 (기본 5s)
          --auth-timeout      duration                 (기본 10s)
          --report-interval   duration                 (기본 10s)
          --stall-timeout     duration                 (기본 30s)
          --login-concurrency int                      (기본 32)     [full 모드]
          --reconnect-delay   duration                 (기본 3s, 지수 백오프 x2, cap 60s)
          --accounts          int                      (기본 3000)
          --token-ttl         duration                 (기본 1h)     [game 모드]
          --rtt-p50-max       duration                 (기본 100ms)
          --rtt-p95-max       duration                 (기본 250ms)
          --rtt-p99-max       duration                 (기본 500ms)
          --min-retention     0..1                     (기본 0.99)
          --max-error-rate    0..1                     (기본 0.001)
          --server-pid        int  | --server-process 이름  (선택: 서버 리소스 샘플링)
          --server-max-ws-mb  int                      (선택: 서버 워킹셋 판정 규칙)
          --out               디렉터리                 (기본 logs)
          --max-log-mb        int                      (기본 256)
        [멀티프로세스·멀티포트 용량 하네스]
          --workers           int                      (기본 1; >1이면 코디네이터)
          --role              auto|coordinator|worker  (기본 auto)
          --worker-index      int                      (워커 전용, < --workers)
          --port-count        int                      (기본 1; 클라 i → game-port + i%P)
          --client-index-offset int                    (코디네이터가 워커에 주입)
          --capacity                                   (용량 판정 모드)
          --target-concurrent int                      (용량 목표 동시 연결, 기본 --clients)
        [스트레스 테스트]
          --stress            burst|churn|malformed|slowloris
          --probe-clients     int                      (기본 200; 정상 대조군)
          --stress-clients    int                      (기본 4000; malformed/slowloris 풀)
          --stress-target     int                      (burst/churn 과부하 목표)
          --baseline-duration duration                 (기본 30s)
          --stress-duration   duration                 (기본 60s)
          --recovery-duration duration                 (기본 90s)
          --probe-ping-interval duration               (기본 1s)
          --slowloris-mode    silent|drip              (기본 silent)
          --help
        종료 코드: 0 PASS · 1 FAIL · 2 사용법/구성 오류 · 3 지속시간 전 중단
        """;

    /// <summary>CLI 인자 배열을 파싱합니다.</summary>
    /// <param name="args">명령줄 인자.</param>
    /// <param name="options">성공 시 파싱된 옵션, 실패 시 null.</param>
    /// <param name="error">실패 시 원인 메시지(<c>--help</c> 요청 시 null + options null).</param>
    /// <returns>파싱 성공 여부. <c>--help</c>는 false를 반환하되 error도 null이다.</returns>
    /// <remarks>
    /// <b>[Thread Safety:]</b> Thread-safe(정적 순수 함수). <b>[Blocking:]</b> Non-blocking.
    /// </remarks>
    public static bool TryParse(string[] args, out LoadTestOptions? options, out string? error)
    {
        options = null;
        error = null;
        var result = new LoadTestOptions();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--help" or "-h" or "/?":
                    return false; // error == null → 호출자가 사용법만 출력

                case "--no-telemetry":
                    result = result with { NoTelemetry = true };
                    continue;

                case "--capacity":
                    result = result with { Capacity = true };
                    continue;

                case "--churn":
                    result = result with { Churn = true };
                    continue;
            }

            // 이하 옵션은 전부 값이 필요하다.
            if (i + 1 >= args.Length)
            {
                error = $"옵션 {arg}에 값이 없습니다.";
                return false;
            }
            string value = args[++i];

            switch (arg)
            {
                case "--mode":
                    if (value is not ("full" or "game"))
                    {
                        error = $"--mode는 full 또는 game이어야 합니다: {value}";
                        return false;
                    }
                    result = result with { Mode = value };
                    break;

                case "--clients":
                    if (!TryParsePositiveInt(value, out int clients)) { error = $"--clients 값이 잘못됨: {value}"; return false; }
                    result = result with { Clients = clients };
                    break;

                case "--ramp-up":
                    if (!TryParsePositiveInt(value, out int ramp)) { error = $"--ramp-up 값이 잘못됨: {value}"; return false; }
                    result = result with { RampUpPerSecond = ramp };
                    break;

                case "--duration":
                    if (!TryParseDuration(value, out var duration)) { error = $"--duration 값이 잘못됨: {value}"; return false; }
                    result = result with { Duration = duration };
                    break;

                case "--host":
                    result = result with { Host = value };
                    break;

                case "--game-port":
                    if (!TryParsePort(value, out int gamePort)) { error = $"--game-port 값이 잘못됨: {value}"; return false; }
                    result = result with { GamePort = gamePort };
                    break;

                case "--auth-port":
                    if (!TryParsePort(value, out int authPort)) { error = $"--auth-port 값이 잘못됨: {value}"; return false; }
                    result = result with { AuthPort = authPort };
                    break;

                case "--telemetry-port":
                    if (!TryParsePort(value, out int telePort)) { error = $"--telemetry-port 값이 잘못됨: {value}"; return false; }
                    result = result with { TelemetryPort = telePort };
                    break;

                case "--ping-interval":
                    if (!TryParseDuration(value, out var ping)) { error = $"--ping-interval 값이 잘못됨: {value}"; return false; }
                    result = result with { PingInterval = ping };
                    break;

                case "--auth-timeout":
                    if (!TryParseDuration(value, out var authTimeout)) { error = $"--auth-timeout 값이 잘못됨: {value}"; return false; }
                    result = result with { AuthTimeout = authTimeout };
                    break;

                case "--report-interval":
                    if (!TryParseDuration(value, out var report)) { error = $"--report-interval 값이 잘못됨: {value}"; return false; }
                    result = result with { ReportInterval = report };
                    break;

                case "--stall-timeout":
                    if (!TryParseDuration(value, out var stall)) { error = $"--stall-timeout 값이 잘못됨: {value}"; return false; }
                    result = result with { StallTimeout = stall };
                    break;

                case "--login-concurrency":
                    if (!TryParsePositiveInt(value, out int loginConc)) { error = $"--login-concurrency 값이 잘못됨: {value}"; return false; }
                    result = result with { LoginConcurrency = loginConc };
                    break;

                case "--reconnect-delay":
                    if (!TryParseDuration(value, out var reconnect)) { error = $"--reconnect-delay 값이 잘못됨: {value}"; return false; }
                    result = result with { ReconnectDelay = reconnect };
                    break;

                case "--accounts":
                    if (!TryParsePositiveInt(value, out int accounts)) { error = $"--accounts 값이 잘못됨: {value}"; return false; }
                    result = result with { Accounts = accounts };
                    break;

                case "--token-ttl":
                    if (!TryParseDuration(value, out var ttl)) { error = $"--token-ttl 값이 잘못됨: {value}"; return false; }
                    result = result with { TokenTtl = ttl };
                    break;

                case "--rtt-p50-max":
                    if (!TryParseDuration(value, out var p50)) { error = $"--rtt-p50-max 값이 잘못됨: {value}"; return false; }
                    result = result with { RttP50Max = p50 };
                    break;

                case "--rtt-p95-max":
                    if (!TryParseDuration(value, out var p95)) { error = $"--rtt-p95-max 값이 잘못됨: {value}"; return false; }
                    result = result with { RttP95Max = p95 };
                    break;

                case "--rtt-p99-max":
                    if (!TryParseDuration(value, out var p99)) { error = $"--rtt-p99-max 값이 잘못됨: {value}"; return false; }
                    result = result with { RttP99Max = p99 };
                    break;

                case "--min-retention":
                    if (!TryParseRatio(value, out double retention)) { error = $"--min-retention 값이 잘못됨(0..1): {value}"; return false; }
                    result = result with { MinRetention = retention };
                    break;

                case "--max-error-rate":
                    if (!TryParseRatio(value, out double errorRate)) { error = $"--max-error-rate 값이 잘못됨(0..1): {value}"; return false; }
                    result = result with { MaxErrorRate = errorRate };
                    break;

                case "--server-pid":
                    if (!TryParsePositiveInt(value, out int pid)) { error = $"--server-pid 값이 잘못됨: {value}"; return false; }
                    result = result with { ServerPid = pid };
                    break;

                case "--server-process":
                    result = result with { ServerProcessName = value };
                    break;

                case "--server-max-ws-mb":
                    if (!TryParsePositiveInt(value, out int maxWs)) { error = $"--server-max-ws-mb 값이 잘못됨: {value}"; return false; }
                    result = result with { ServerMaxWorkingSetMb = maxWs };
                    break;

                case "--out":
                    result = result with { OutDirectory = value };
                    break;

                case "--max-log-mb":
                    if (!TryParsePositiveInt(value, out int maxLog)) { error = $"--max-log-mb 값이 잘못됨: {value}"; return false; }
                    result = result with { MaxLogMb = maxLog };
                    break;

                case "--workers":
                    if (!TryParsePositiveInt(value, out int workers)) { error = $"--workers 값이 잘못됨: {value}"; return false; }
                    result = result with { Workers = workers };
                    break;

                case "--role":
                    if (value is not ("auto" or "coordinator" or "worker"))
                    {
                        error = $"--role은 auto|coordinator|worker여야 합니다: {value}";
                        return false;
                    }
                    result = result with { Role = value };
                    break;

                case "--worker-index":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int workerIdx) || workerIdx < 0)
                    { error = $"--worker-index 값이 잘못됨: {value}"; return false; }
                    result = result with { WorkerIndex = workerIdx };
                    break;

                case "--port-count":
                    if (!TryParsePositiveInt(value, out int portCount) || portCount > 64)
                    { error = $"--port-count 값이 잘못됨(1..64): {value}"; return false; }
                    result = result with { PortCount = portCount };
                    break;

                case "--client-index-offset":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int offset) || offset < 0)
                    { error = $"--client-index-offset 값이 잘못됨: {value}"; return false; }
                    result = result with { ClientIndexOffset = offset };
                    break;

                case "--target-concurrent":
                    if (!TryParsePositiveInt(value, out int target)) { error = $"--target-concurrent 값이 잘못됨: {value}"; return false; }
                    result = result with { TargetConcurrent = target };
                    break;

                case "--source-port-base":
                    if (!TryParsePort(value, out int srcBase)) { error = $"--source-port-base 값이 잘못됨: {value}"; return false; }
                    result = result with { SourcePortBase = srcBase };
                    break;

                case "--stress":
                    switch (value)
                    {
                        case "burst": result = result with { Stress = StressScenarioKind.Burst }; break;
                        case "churn": result = result with { Stress = StressScenarioKind.Churn }; break;
                        case "malformed": result = result with { Stress = StressScenarioKind.Malformed }; break;
                        case "slowloris": result = result with { Stress = StressScenarioKind.Slowloris }; break;
                        default: error = $"--stress는 burst|churn|malformed|slowloris여야 합니다: {value}"; return false;
                    }
                    break;

                case "--probe-clients":
                    if (!TryParsePositiveInt(value, out int probe)) { error = $"--probe-clients 값이 잘못됨: {value}"; return false; }
                    result = result with { ProbeClients = probe };
                    break;

                case "--stress-clients":
                    if (!TryParsePositiveInt(value, out int stressClients)) { error = $"--stress-clients 값이 잘못됨: {value}"; return false; }
                    result = result with { StressClients = stressClients };
                    break;

                case "--stress-target":
                    if (!TryParsePositiveInt(value, out int stressTarget)) { error = $"--stress-target 값이 잘못됨: {value}"; return false; }
                    result = result with { StressTarget = stressTarget };
                    break;

                case "--baseline-duration":
                    if (!TryParseDuration(value, out var baseline)) { error = $"--baseline-duration 값이 잘못됨: {value}"; return false; }
                    result = result with { BaselineDuration = baseline };
                    break;

                case "--stress-duration":
                    if (!TryParseDuration(value, out var stressDur)) { error = $"--stress-duration 값이 잘못됨: {value}"; return false; }
                    result = result with { StressDuration = stressDur };
                    break;

                case "--recovery-duration":
                    if (!TryParseDuration(value, out var recovery)) { error = $"--recovery-duration 값이 잘못됨: {value}"; return false; }
                    result = result with { RecoveryDuration = recovery };
                    break;

                case "--probe-ping-interval":
                    if (!TryParseDuration(value, out var probePing)) { error = $"--probe-ping-interval 값이 잘못됨: {value}"; return false; }
                    result = result with { ProbePingInterval = probePing };
                    break;

                case "--slowloris-mode":
                    if (value is not ("silent" or "drip")) { error = $"--slowloris-mode는 silent|drip여야 합니다: {value}"; return false; }
                    result = result with { SlowlorisMode = value };
                    break;

                default:
                    error = $"알 수 없는 옵션: {arg}";
                    return false;
            }
        }

        // 교차 검증: 워커 인덱스는 워커 수 미만, 게임 포트 범위는 65535 이내.
        if (result.WorkerIndex >= result.Workers)
        {
            error = $"--worker-index({result.WorkerIndex})는 --workers({result.Workers}) 미만이어야 합니다.";
            return false;
        }
        if (result.GamePort + result.PortCount - 1 > 65535)
        {
            error = $"게임 포트 범위가 65535를 초과합니다(--game-port {result.GamePort} + --port-count {result.PortCount}).";
            return false;
        }

        options = result;
        return true;
    }

    /// <summary>"72h"/"30m"/"60s"/"500ms" 형식의 기간 문자열을 파싱합니다(대소문자 무시, 소수 허용).</summary>
    internal static bool TryParseDuration(string text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrEmpty(text))
            return false;

        // "ms"를 "m"보다 먼저 검사해야 "500ms"가 분(minute)으로 오파싱되지 않는다.
        (string suffix, double toMs)[] units =
        [
            ("ms", 1), ("s", 1_000), ("m", 60_000), ("h", 3_600_000),
        ];

        foreach (var (suffix, toMs) in units)
        {
            if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            string number = text[..^suffix.Length];
            // InvariantCulture: 소수점 기호가 문화권에 따라 ','가 되어 "1.5h" 파싱이 깨지는 것을 방지
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                return false;
            if (parsed < 0 || double.IsNaN(parsed) || double.IsInfinity(parsed))
                return false;
            value = TimeSpan.FromMilliseconds(parsed * toMs);
            return true;
        }

        return false;
    }

    private static bool TryParsePositiveInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > 0;

    private static bool TryParsePort(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value is > 0 and <= 65535;

    private static bool TryParseRatio(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value is >= 0 and <= 1;
}
