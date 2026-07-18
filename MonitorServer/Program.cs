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
//   브라우저: http://127.0.0.1:8080  (포트는 IDLERPG_MONITOR_WEB_PORT로 재정의 가능)
// =============================================================================

using System.Text.Json;
using System.Text.Json.Serialization;
using MonitorServer;

const string GameServerHost = "127.0.0.1"; // 루프백 고정 — GameServer 텔레메트리 리스너도 루프백 전용(Main.cs 참고).
const int TelemetryPort = 7779; // GameServer/Main.cs의 TelemetryPort 상수와 반드시 일치해야 한다.
var reconnectDelay = TimeSpan.FromSeconds(2); // GameServer 미기동/재시작 시 재접속 간격.

int webPort = int.TryParse(Environment.GetEnvironmentVariable("IDLERPG_MONITOR_WEB_PORT"), out var parsedPort)
    ? parsedPort
    : 8080;

// JsonSerializerOptions(camelCase): DashboardHtml.cs의 JS가 s.bossCurrentHp처럼 camelCase 필드명을
// 읽으므로, System.Text.Json 기본 PascalCase 대신 명시적으로 맞춰야 한다. 재사용 가능한 옵션
// 인스턴스이므로(스레드 안전) SSE 핸들러가 매 틱 새로 만들지 않고 이 인스턴스를 공유한다.
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

var store = new TelemetrySnapshotStore();

// CancellationTokenSource: Ctrl+C(SIGINT) 기본 동작인 즉시 프로세스 종료 대신 협조적 취소로 바꾼다
// (GameServer/Main.cs, AuthServer/Program.cs와 동일한 종료 패턴).
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Task.Run(fire-and-forget): 텔레메트리 재접속 루프는 웹 서버 수명 내내 백그라운드에서 독립적으로
// 돈다 — WebApplication.Run()이 스레드를 점유하는 동안에도 계속 재접속을 시도한다.
_ = Task.Run(() => TelemetryClientLoop.RunAsync(GameServerHost, TelemetryPort, store, reconnectDelay, cts.Token), cts.Token);

var builder = WebApplication.CreateBuilder();
// 콘솔 노이즈 억제: 이 프로세스는 오직 대시보드 서빙만 하므로 ASP.NET Core 기본 요청 로깅이 불필요하다
// (GameServer가 2026-07-07 관측성 전환 이후 콘솔 직접 출력을 하지 않는 것과 같은 방향의 결정).
builder.Logging.ClearProviders();
var app = builder.Build();

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

app.Run($"http://127.0.0.1:{webPort}");
