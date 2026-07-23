// =============================================================================
// WebClient — 브라우저 게스트 플레이 게이트웨이 (WebSocket ↔ GameServer TCP 브리지)
// =============================================================================
// 설계: plan/web_client_0723.md. GameServer(프로세스 A)와 완전히 분리된 별도 프로세스로,
// 게임 페이지(GET /)를 서빙하고 브라우저당 WebSocket 1개(GET /ws)를 GameServer 게임 포트(7777)의
// TCP 연결 1개로 브리지한다. 게스트 로그인은 AuthServer/MongoDB 없이 이 프로세스가
// HmacAuthTokenCodec으로 직접 토큰을 발급한다(LoadTester --mode game과 동일 원리) — GameServer와
// 동일한 HMAC 비밀키(HmacSecretResolver 정책)를 공유해야 인증 게이트를 통과한다.
// 웹 의존성(ASP.NET Core)은 MonitorServer와 이 프로젝트에만 존재한다 — GameServer/ServerLib는
// 여전히 외부 의존성 0을 유지한다.
//
// 실행법:
//   dotnet run --project GameServer   (먼저 실행 — 없으면 입장 시 연결 오류 안내)
//   dotnet run --project WebClient
//   브라우저: http://127.0.0.1:8081  (IDLERPG_WEBCLIENT_WEB_PORT/IDLERPG_WEBCLIENT_WEB_BIND로 재정의)
//   Docker: IDLERPG_WEBCLIENT_GAME_HOST/IDLERPG_WEBCLIENT_GAME_PORT로 GameServer 접속 대상 재정의
// =============================================================================

using System.Runtime.InteropServices;
using ServerLib.Core.Auth;
using WebClient;

string gameHost = Environment.GetEnvironmentVariable("IDLERPG_WEBCLIENT_GAME_HOST") ?? "127.0.0.1";
int gamePort = int.TryParse(Environment.GetEnvironmentVariable("IDLERPG_WEBCLIENT_GAME_PORT"), out var envGamePort)
    ? envGamePort
    : 7777; // GameServer/Main.cs의 게임 포트 기본값과 반드시 일치해야 한다.

int webPort = int.TryParse(Environment.GetEnvironmentVariable("IDLERPG_WEBCLIENT_WEB_PORT"), out var parsedPort)
    ? parsedPort
    : 8081; // MonitorServer(8080)와 나란히 로컬 동시 실행 가능하도록 +1.
// 기본 바인드는 루프백(로컬 실행 보안 보존) — Docker에서만 0.0.0.0으로 재정의(docker-compose.yml).
string webBind = Environment.GetEnvironmentVariable("IDLERPG_WEBCLIENT_WEB_BIND") ?? "127.0.0.1";

// HMAC 비밀키: GameServer와 동일 정책(env 우선 → DEBUG 폴백 → 실패)으로 해석. 실패 시 fail-fast —
// 잘못된 키로 떠 있으면 모든 게스트 인증이 조용히 거부되는 반쪽 가동 상태가 되기 때문이다.
if (!HmacSecretResolver.TryResolve(out byte[] hmacSecret, out HmacSecretResolver.SecretSource secretSource, out string? secretError))
{
    Console.Error.WriteLine($"[치명] HMAC 비밀키 해석 실패: {secretError}");
    return 1;
}
if (secretSource == HmacSecretResolver.SecretSource.DevFallback)
    Console.WriteLine("[경고] 개발용 기본 HMAC 비밀키 사용 중 — 배포 시 IDLERPG_AUTH_HMAC_SECRET을 반드시 설정하세요.");

Console.WriteLine($"[초기화] 설정 로딩 완료 - web={webBind}:{webPort}, gameServer={gameHost}:{gamePort}");

var directory = new GuestDirectory();
var issuer = new GuestTokenIssuer(new HmacAuthTokenCodec(hmacSecret), directory, tokenTtl: TimeSpan.FromMinutes(10));
var bridge = new GameBridge(issuer, directory, gameHost, gamePort);
Console.WriteLine("[초기화] 게스트 토큰 발급기·브리지 생성 완료.");

// CancellationTokenSource: Ctrl+C(SIGINT) 기본 동작인 즉시 프로세스 종료 대신 협조적 취소로 바꾼다
// (GameServer/MonitorServer와 동일한 종료 패턴).
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
// PosixSignalRegistration(SIGTERM): docker compose down/stop 신호는 SIGINT가 아니라 SIGTERM이라
// CancelKeyPress만으로는 잡히지 않는다(MonitorServer와 동일 이유).
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    cts.Cancel();
});

var builder = WebApplication.CreateBuilder();
// 콘솔 노이즈 억제: 페이지 서빙 + WS 브리지만 하므로 ASP.NET Core 기본 요청 로깅이 불필요하다.
builder.Logging.ClearProviders();
var app = builder.Build();

// WebSocket 미들웨어: KeepAliveInterval은 WS 레벨 ping(브라우저 자동 응답)으로 프록시/NAT 유휴
// 절단을 막는다 — 게임 TCP 쪽 생존 신호(IClientConnection.PingInterval)와는 별개 계층이다.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// GET /: 단일 페이지 게임 HTML을 그대로 반환(정적 파일 미들웨어 없이 인메모리 상수 문자열).
app.MapGet("/", () => Results.Content(GameHtml.Page, "text/html; charset=utf-8"));

// GET /ws: 브라우저당 브리지 1건. 여러 탭이 동시에 붙어도 각자 자신의 요청 스레드에서 이 델리게이트를
// 독립 실행하며, 접속별 상태는 전부 GameBridge.RunAsync 지역에 있다.
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
    // CreateLinkedTokenSource: 탭 닫힘(RequestAborted)과 프로세스 셧다운(cts.Token) 중 먼저 오는
    // 신호로 브리지를 끝낸다(MonitorServer /events와 동일 패턴).
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, cts.Token);
    await bridge.RunAsync(new WebSocketBrowserChannel(socket), linked.Token);
});

Console.WriteLine($"[가동] WebClient 게스트 플레이 시작 -> http://{webBind}:{webPort} (game {gameHost}:{gamePort})");
Console.WriteLine("[가동] WebClient 초기화 완료 - 브라우저에서 접속해 게스트로 입장하세요.");
app.Run($"http://{webBind}:{webPort}");
return 0;
