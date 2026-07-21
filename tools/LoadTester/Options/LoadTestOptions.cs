using System.Globalization;

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

                default:
                    error = $"알 수 없는 옵션: {arg}";
                    return false;
            }
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
