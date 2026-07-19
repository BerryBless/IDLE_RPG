// =============================================================================
// MonitorServer — GameServer 텔레메트리 구독 + 웹 실시간 모니터링 대시보드
// =============================================================================
// 설계: plan/web_monitoring_0718.md. GameServer(프로세스 A)와 완전히 분리된 별도 프로세스(B)로,
// GameServer의 텔레메트리 리스너(포트 7779, 읽기 전용)를 ServerLib 클라이언트로 구독해 접속자
// 수·리스너 통계·공유 레이드 보스 HP/세대/MVP를 받아 브라우저에 Server-Sent Events로 실시간
// 중계한다. 웹 의존성(ASP.NET Core)은 이 프로젝트에만 존재한다 — GameServer/ServerLib는 여전히
// 외부 의존성 0을 유지한다(설계의 핵심 이득, plan 문서 참고).
//
// 실행법:
//   dotnet run --project GameServer   (먼저 실행해 두어야 텔레메트리를 받을 수 있음 — 없어도 기동은 됨)
//   dotnet run --project MonitorServer
//   브라우저: http://127.0.0.1:8080  (포트는 IDLERPG_MONITOR_WEB_PORT, 바인드는 IDLERPG_MONITOR_WEB_BIND로 재정의 가능)
//   Docker: IDLERPG_MONITOR_GAME_HOST/IDLERPG_MONITOR_GAME_TELEMETRY_PORT로 GameServer 접속 대상 재정의
//   (docker-compose.yml, plan/docker_compose_0719.md 참고)
// =============================================================================

using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using MonitorServer;

// GameServerHost/TelemetryPort는 환경 변수로 재정의 가능(Docker 컨테이너화, plan/docker_compose_0719.md).
// 기본값은 기존 로컬 실행과 동일한 루프백 — GameServer 텔레메트리 리스너도 기본이 루프백이므로
// (GameServer/Main.cs 참고) 로컬에서는 값을 바꿀 필요가 없다. docker-compose에서는
// IDLERPG_MONITOR_GAME_HOST=gameserver(서비스명 DNS)로 재정의한다.
string gameServerHost = Environment.GetEnvironmentVariable("IDLERPG_MONITOR_GAME_HOST") ?? "127.0.0.1";
int telemetryPort = int.TryParse(Environment.GetEnvironmentVariable("IDLERPG_MONITOR_GAME_TELEMETRY_PORT"), out var envTelemetryPort)
    ? envTelemetryPort
    : 7779; // GameServer/Main.cs의 telemetryPort 기본값과 반드시 일치해야 한다.
var reconnectDelay = TimeSpan.FromSeconds(2); // GameServer 미기동/재시작 시 재접속 간격.

int webPort = int.TryParse(Environment.GetEnvironmentVariable("IDLERPG_MONITOR_WEB_PORT"), out var parsedPort)
    ? parsedPort
    : 8080;
// 대시보드 웹 서버 바인드 주소. 기본값은 루프백(기존 로컬 실행과 동일). Docker에서는
// IDLERPG_MONITOR_WEB_BIND=0.0.0.0으로 재정의해 호스트에 publish된 포트로 접속을 받는다.
string webBind = Environment.GetEnvironmentVariable("IDLERPG_MONITOR_WEB_BIND") ?? "127.0.0.1";
Console.WriteLine(
    $"[초기화] 설정 로딩 완료 - web={webBind}:{webPort}, telemetrySource={gameServerHost}:{telemetryPort}, " +
    $"reconnectDelay={reconnectDelay.TotalSeconds:0}s");

// JsonSerializerOptions(camelCase): DashboardHtml.cs의 JS가 s.bossCurrentHp처럼 camelCase 필드명을
// 읽으므로, System.Text.Json 기본 PascalCase 대신 명시적으로 맞춰야 한다. 재사용 가능한 옵션
// 인스턴스이므로(스레드 안전) SSE 핸들러가 매 틱 새로 만들지 않고 이 인스턴스를 공유한다.
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

var store = new TelemetrySnapshotStore();
Console.WriteLine("[초기화] TelemetrySnapshotStore 생성 완료(아직 구독 시작 전 - 최초 스냅샷은 기본값).");

// CancellationTokenSource: Ctrl+C(SIGINT) 기본 동작인 즉시 프로세스 종료 대신 협조적 취소로 바꾼다
// (GameServer/Main.cs, AuthServer/Program.cs와 동일한 종료 패턴).
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
// PosixSignalRegistration(SIGTERM): docker compose down/stop이 보내는 신호는 SIGINT가 아니라
// SIGTERM이라 위 CancelKeyPress만으로는 잡히지 않는다 — GameServer/Main.cs와 동일 이유로 등록
// (Windows 로컬 dotnet run에는 SIGTERM 자체가 없어 영향 없음).
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    cts.Cancel();
});

// Task.Run(fire-and-forget): 텔레메트리 재접속 루프는 웹 서버 수명 내내 백그라운드에서 독립적으로
// 돈다 — WebApplication.Run()이 스레드를 점유하는 동안에도 계속 재접속을 시도한다.
_ = Task.Run(() => TelemetryClientLoop.RunAsync(gameServerHost, telemetryPort, store, reconnectDelay, cts.Token), cts.Token);
Console.WriteLine($"[초기화] 텔레메트리 재접속 루프 기동 완료 -> {gameServerHost}:{telemetryPort} 구독 시도 시작(GameServer 미기동이어도 계속 재시도).");

var builder = WebApplication.CreateBuilder();
// 콘솔 노이즈 억제: 이 프로세스는 오직 대시보드 서빙만 하므로 ASP.NET Core 기본 요청 로깅이 불필요하다
// (GameServer가 2026-07-07 관측성 전환 이후 콘솔 직접 출력을 하지 않는 것과 같은 방향의 결정).
builder.Logging.ClearProviders();
var app = builder.Build();
Console.WriteLine("[초기화] ASP.NET Core WebApplication 빌드 완료(기본 요청 로깅은 억제됨).");

// GET /: 단일 페이지 대시보드 HTML을 그대로 반환한다(정적 파일 미들웨어 없이 인메모리 상수 문자열).
app.MapGet("/", () => Results.Content(DashboardHtml.Page, "text/html; charset=utf-8"));

// GET /events: text/event-stream으로 최신 스냅샷을 1초 주기로 푸시한다. 여러 브라우저 탭이 동시에
// 구독해도 각자 자신의 요청 스레드에서 이 델리게이트를 독립 실행하므로 서로 간섭하지 않는다
// (store.Current는 Thread-safe 읽기 전용 스냅샷 참조).
app.MapGet("/events", async (HttpContext ctx, CancellationToken requestAborted) =>
{
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.ContentType = "text/event-stream";

    // CreateLinkedTokenSource: 브라우저가 탭을 닫아 requestAborted가 취소되거나, 이 프로세스가
    // 종료(cts.Token)되거나 — 둘 중 먼저 오는 신호로 루프를 끝낸다.
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, cts.Token);
    try
    {
        while (!linked.IsCancellationRequested)
        {
            string json = JsonSerializer.Serialize(store.Current, jsonOptions);
            await ctx.Response.WriteAsync($"data: {json}\n\n", linked.Token);
            await ctx.Response.Body.FlushAsync(linked.Token); // SSE는 명시적 flush 없이는 프록시/버퍼에 갇혀 브라우저에 늦게 도달할 수 있다.
            await Task.Delay(TimeSpan.FromSeconds(1), linked.Token);
        }
    }
    catch (OperationCanceledException)
    {
        // 정상 종료(탭 닫힘 또는 서버 셧다운) — 예외를 그대로 삼키고 핸들러를 반환한다.
    }
});

Console.WriteLine("[초기화] 라우트 등록 완료 (GET / 대시보드, GET /events SSE).");

// Kestrel 기본 "Now listening on..." 로그는 위 ClearProviders()로 함께 꺼진다 — 여러 서버를 동시에
// 띄울 때(run-local.bat) 어느 창인지, 어느 주소에 떠 있는지 구분할 수 있도록 대체 상태 줄을 남긴다.
Console.WriteLine($"[가동] MonitorServer 대시보드 시작 -> http://{webBind}:{webPort} (telemetry source {gameServerHost}:{telemetryPort})");
Console.WriteLine("[가동] MonitorServer 초기화 완료 - 모든 컴포넌트가 정상 기동되었습니다.");
app.Run($"http://{webBind}:{webPort}");
