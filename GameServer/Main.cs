// BigNumber는 double 별칭이다. 방치형 특성상 수치가 매우 커질 수 있어 전용 struct 도입 여지를
// 남겨둔다 — 실제 도입은 인플레이션이 double 정밀도(약 15~17자리)를 위협하는 시점에 재검토한다.
global using BigNumber = double;

using System.Net;
using System.Runtime.InteropServices;
using GameServer.Items;
using GameServer.Systems;
using ServerLib;
using ServerLib.Core.Auth;
using ServerLib.Core.Serialization;
using ServerLib.Interface;

// 클라-서버 분리 1단계: 이전까지 GameServer는 400명의 가상 플레이어가 스레드 샤딩으로 자동
// 전투하는 콘솔 데모였다(스레드 샤딩 배틀/레이드 코드는 git 이력에 보존, Systems/BattleLoop.cs
// 등 도메인 클래스 자체는 그대로 유지되며 각자의 단위 테스트가 계속 커버한다). 그 사이클에서는
// 실제 TCP 클라이언트가 접속할 수 있는 네트워크 서버로 전환했다.
//
// 전투 멀티플레이 1단계: 접속한 각 클라이언트가 자신만의 독립적인 몬스터를 서버 자동 틱으로
// 동시에 사냥하도록 만들었다(SessionBattleRunner) — 그 클래스와 테스트는 git 이력에 보존되며
// 이 서버 경로에서는 더 이상 배선하지 않는다.
//
// 전투 멀티플레이 2단계: 접속한 모든 클라이언트가 하나의 공유 레이드 보스(몬스터 7001)를 동시에
// 공격한다(SessionRaidRunner) — 보스 HP/처치를 ISessionRegistry로 전 세션에 브로드캐스트한다.
// 보스는 반격하지 않으므로(Atk=0) 플레이어는 죽지 않는다(순수 DPS 레이스). 제한시간 내 미처치 시
// 보상 없이 리셋(RaidFailed).
//
// 토큰 게이트(이번 사이클): 별도 AuthServer(plan/login_mongo_0709.md)가 발급한 HMAC 토큰을
// AuthTokenPacket으로 받아 SessionAuthGate가 검증하고, 성공한 세션에만 실제 Player(인증된
// AccountId)를 결합해 레이드 참전을 허용한다(plan/gameserver_auth_gate_0709.md). 접속 즉시 임시
// Player를 만들던 SessionPlayerBinder.OnConnected/PlayerFactory.CreateTemp 경로는 더 이상
// 배선하지 않는다(메서드 자체는 다른 테스트의 픽스처 헬퍼로 계속 쓰이므로 삭제하지 않음).
//
// 웹 모니터링 텔레메트리(이번 사이클, plan/web_monitoring_0718.md): 게임 리스너(7777)와 완전히
// 분리된 두 번째 읽기 전용 리스너(7779)를 열어, 별도 MonitorServer 프로세스가 ServerLib 클라이언트로
// 구독하면 TelemetryPublisher가 1초 주기로 접속자 수·리스너 통계·공유 보스 HP/세대/MVP를
// TelemetrySnapshotPacket으로 브로드캐스트한다. 텔레메트리 리스너는 인증 게이트가 없다(읽기 전용,
// 루프백 한정) — OnReceived를 배선하지 않아 모니터가 보내는 데이터는 애초에 처리되지 않는다.
// PvP·회원가입·스테이지 시스템은 이후 사이클 과제다.

// 포트/바인드는 환경 변수로 재정의 가능(Docker 컨테이너화, plan/docker_compose_0719.md) — 값이
// 없으면 기존 로컬 실행 기본값을 그대로 쓴다.
int gamePort = int.TryParse(Environment.GetEnvironmentVariable("IDLERPG_GAME_PORT"), out var envGamePort)
    ? envGamePort
    : 7777; // examples/EchoServer(9000)와 겹치지 않도록 구분 — 필요 시 동시 실행 가능.
int telemetryPort = int.TryParse(Environment.GetEnvironmentVariable("IDLERPG_GAME_TELEMETRY_PORT"), out var envTelemetryPort)
    ? envTelemetryPort
    : 7779; // 게임 리스너(7777)와 겹치지 않는 별도 포트 — 웹 모니터링 전용 읽기 전용 구독.
// ConsoleStatusReporter 출력 주기(초). 0이면 완전히 비활성화 — Docker처럼 로그 드라이버가 stdout을
// 별도로 수집·과금하는 환경에서 반복 출력 자체를 끄고 싶을 때 쓴다.
int consoleStatusIntervalSeconds = int.TryParse(
    Environment.GetEnvironmentVariable("IDLERPG_GAME_CONSOLE_INTERVAL_SECONDS"), out var envConsoleInterval)
    ? envConsoleInterval
    : 5;
// IPAddress.Parse("127.0.0.1")는 IPAddress.Loopback과 동일값이므로 기본 동작은 이전과 같다.
// Docker에서는 IDLERPG_GAME_BIND=0.0.0.0으로 재정의해 컨테이너 밖(다른 서비스)에서 접속을 받는다.
var bindAddress = IPAddress.Parse(Environment.GetEnvironmentVariable("IDLERPG_GAME_BIND") ?? "127.0.0.1");
var raidTimeLimit = TimeSpan.FromSeconds(60); // 이 시간 내에 전원이 힘을 모아 보스를 잡아야 한다.

// 용량 테스트 모드(opt-in, plan/capacity_harness_0721.md): 대규모 동시 연결 순수 용량 측정용.
// IDLERPG_GAME_CAPACITY_MODE=1이면 인증 성공 후 레이드 브로드캐스트를 배선하지 않는다 —
// 공유 보스 브로드캐스트는 registry.BroadcastAsync O(N) 팬아웃이라 수십만 세션에서 붕괴하기 때문.
// 이 모드에서는 "연결 + HMAC 인증 + 유지"만 하며, 게임플레이 로직은 돌지 않는다. 기본값 false는
// 기존 동작을 완전히 보존한다.
bool capacityMode = Environment.GetEnvironmentVariable("IDLERPG_GAME_CAPACITY_MODE") == "1";
// 하드닝(opt-in, plan/stress_harness_0721.md §6): 스트레스 테스트가 드러낸 약점(정체/느린 세션 무축출,
// 연결 폭주 상한 없음)에 대한 방어. 기본 미설정 = 방어 없음(기존 동작). before/after 비교를 위해 env로 토글.
//   IDLERPG_GAME_IDLE_TIMEOUT_SECONDS: 진행(완전 패킷) 기준 유휴 스윕 — PING하는 정상 클라는 안전,
//     slowloris(무송신)·악성 미완성 플러드(완전 프레임 미완성)는 LastProgressAt 미갱신이라 축출된다.
//     반드시 정상 클라의 PING 주기보다 커야 한다(안 그러면 held 정상 세션도 축출됨).
//   IDLERPG_GAME_MAX_CONNECTIONS: 전체 동시 연결 상한(초과 연결은 세션 할당 전에 거부 — 저비용).
int? idleTimeoutSeconds = int.TryParse(
    Environment.GetEnvironmentVariable("IDLERPG_GAME_IDLE_TIMEOUT_SECONDS"), out var envIdle) && envIdle > 0
    ? envIdle : null;
int? maxConnections = int.TryParse(
    Environment.GetEnvironmentVariable("IDLERPG_GAME_MAX_CONNECTIONS"), out var envMax) && envMax > 0
    ? envMax : null;
//   IDLERPG_GAME_MAX_FRAMES_PER_SECOND: 세션당 초당 최대 프레임 수. 초과 세션을 즉시 끊어 악성 프레임
//     플러드의 CPU/GC 부하를 근본 차단(정상 클라는 인증 1회 + 주기 PING이라 여유 하회). IdleTimeout(연결
//     유지형)·MaxConnections(연결 수)와 함께 malformed 완전 방어를 이룬다.
int? maxFramesPerSecond = int.TryParse(
    Environment.GetEnvironmentVariable("IDLERPG_GAME_MAX_FRAMES_PER_SECOND"), out var envFps) && envFps > 0
    ? envFps : null;
long idleEvictions = 0; // OnIdleTimeout이 증가시키는 축출 카운터(종료 시 리포트).
// 게임 리스너 포트 수: 단일 소스 IP → 단일 서버 엔드포인트는 4-튜플 제약상 임시 포트 수(~수만)로
// 동시 연결이 상한된다. P개 포트를 열면 클라이언트가 포트별로 4-튜플을 분산해 P배까지 확장 가능.
// 기본 1은 기존 단일 포트 동작과 동일.
int gamePortCount = int.TryParse(Environment.GetEnvironmentVariable("IDLERPG_GAME_PORT_COUNT"), out var envPortCount)
    ? Math.Clamp(envPortCount, 1, 64)
    : 1;
// 포트 범위 충돌 가드: 게임 리스너는 [gamePort .. gamePort+gamePortCount-1]을 쓴다. 텔레메트리
// 포트가 이 범위에 겹치면 리스너에 설정된 SO_REUSEADDR 때문에 Windows에서 오류 없이 두 리스너가
// 같은 포트를 바인드해버려(수신이 어느 쪽으로 갈지 미정의) 조용히 오작동한다. 즉시 실패로 막는다.
if (telemetryPort >= gamePort && telemetryPort < gamePort + gamePortCount)
{
    throw new InvalidOperationException(
        $"텔레메트리 포트({telemetryPort})가 게임 리스너 포트 범위" +
        $"[{gamePort}..{gamePort + gamePortCount - 1}]와 겹칩니다. " +
        "IDLERPG_GAME_TELEMETRY_PORT를 이 범위 밖(예: 게임 포트 범위보다 높은 값)으로 설정하세요.");
}
Console.WriteLine(
    $"[초기화] 설정 로딩 완료 - game={bindAddress}:{gamePort}" +
    (gamePortCount > 1 ? $"..{gamePort + gamePortCount - 1}({gamePortCount}포트)" : "") +
    $", telemetry={bindAddress}:{telemetryPort}, " +
    $"raidTimeLimit={raidTimeLimit.TotalSeconds:0}s, consoleStatusInterval=" +
    (consoleStatusIntervalSeconds > 0 ? $"{consoleStatusIntervalSeconds}s" : "disabled") +
    (capacityMode ? ", CAPACITY_MODE=on(레이드 미배선)" : ""));

var levelSystem = PlayerLevelSystem.CreateDefault();
var monsterTable = MonsterTable.CreateDefault();
var equipmentTable = EquipmentTable.CreateDefault();
Console.WriteLine("[초기화] 레벨/몬스터/장비 테이블 로드 완료.");

// GameEventSink: 다수 I/O 스레드(생산자)가 Record*로 메트릭+NDJSON 라인을 밀어넣고, 내부 단일
// 소비자 태스크가 파일에 flush한다. 2026-07-07 관측성 전환 이후 게임 "이벤트"(접속/해제/보상 등
// 런타임 중 반복되는 사건)는 콘솔이 아닌 이 sink로만 기록한다는 정책은 지금도 유지된다.
// 2026-07-19/20: 여기에 별개로 두 가지를 콘솔에 남긴다 — (1) 아래 곳곳의 "[초기화]"/"[가동]"
// Console.WriteLine은 기동 시퀀스를 1회성으로만 상세히 보여주는 것(run-local.bat으로 여러 서버를
// 동시에 띄울 때 어느 창이 어느 단계까지 진행됐는지 눈으로 바로 확인하기 위함), (2)
// ConsoleStatusReporter는 sink의 누적 카운터를 5초 주기로 스냅샷/차분해 집계된 런타임 상태 한 줄만
// 반복 출력한다(아래 consoleStatusIntervalSeconds 사용부 참고). 둘 다 "이벤트 스트림 자체를 매건
// 콘솔로 되돌리는" 것과는 다르다 — 전자는 기동 중 유한 횟수만 찍히고, 후자는 출력 빈도가 이벤트
// 발생량과 무관하게 주기로 상한이 걸린다.
var eventLogPath = Path.Combine("logs", "game-events.ndjson");
await using var sink = GameEventSink.CreateFile(eventLogPath);
Console.WriteLine($"[초기화] 이벤트 싱크 생성 완료 -> {eventLogPath} (게임 이벤트 상세는 콘솔이 아닌 이 파일에 NDJSON으로 기록됨)");

// SessionPlayerBinder: 이제 OnConnected는 배선하지 않는다(SessionAuthGate가 대체) — 연결
// 해제/오류 시 sink에 기록하는 OnDisconnected/OnError만 계속 사용한다.
var binder = new SessionPlayerBinder(levelSystem, sink);

// HMAC 시크릿: AuthServer(AuthServerConfig.HmacSecret)와 반드시 동일한 값을 공유해야 발급된
// 토큰을 이쪽에서 검증할 수 있다. 설정값이 이거 하나뿐이라 Port처럼 인라인으로 읽는다(별도
// Config 클래스 없음). 코드리뷰 Critical 발견 수정
// (docs/code-reviews/2026-07-18-auth-login-and-web-monitoring-review.md): 이전에는 환경 변수가
// 없으면 소스에 하드코딩된(=공개 저장소에 노출된) 개발용 기본키로 조용히 폴백해, 운영에서 설정을
// 빠뜨리면 그 알려진 키로 누구나 토큰을 위조해 인증 게이트를 완전히 우회할 수 있었다. 이제 Release
// 빌드에서는 폴백 없이 즉시 실패(fail-fast)하고, 어떤 경로든 32바이트 미만 키는 거부한다
// 해석 정책(env 우선 → DEBUG 폴백 → Release fail-fast → 32바이트 검증)은 ServerLib의 공용
// HmacSecretResolver로 AuthServer·LoadTester와 단일 소스를 공유한다(이전의 4중 복제 통합 — 코드리뷰 Medium).
var hmacSecret = ResolveHmacSecret();
Console.WriteLine("[초기화] HMAC 토큰 검증 키 로드 완료.");

static byte[] ResolveHmacSecret()
{
    if (!HmacSecretResolver.TryResolve(out byte[] secret, out var source, out string? error))
        throw new InvalidOperationException(error);
    if (source == HmacSecretResolver.SecretSource.DevFallback)
        Console.WriteLine(
            "[경고] IDLERPG_AUTH_HMAC_SECRET 환경 변수가 없어 개발용 기본 비밀키를 사용합니다. " +
            "AuthServer도 동일한 값을 써야 발급된 토큰이 검증됩니다(AuthServer/Program.cs 경고 참고).");
    return secret;
}
// HmacAuthTokenCodec: 발급(IAuthTokenIssuer)도 구현하지만 GameServer는 검증(IAuthTokenValidator)만
// 쓴다 — 토큰 발급은 AuthServer 전용 책임.
var authGate = new SessionAuthGate(new HmacAuthTokenCodec(hmacSecret), levelSystem, sink, new BinaryPacketSerializer());
Console.WriteLine("[초기화] 세션 인증 게이트(SessionAuthGate) 생성 완료.");

// ServerNet.CreateSessionRegistry(): 공유 보스 co-op는 보스 HP/처치를 접속한 모든 세션에
// 브로드캐스트해야 하므로(1단계와 달리 특정 세션 하나에게만 보내는 것으로는 부족) 세션 레지스트리가
// 필수다 — 반드시 CreateListener에도 같은 인스턴스를 넘겨야 리스너가 접속/해제를 자동 등록/해제한다.
var registry = ServerNet.CreateSessionRegistry();

// 텔레메트리 전용 세션 레지스트리: 게임 세션(registry)과 완전히 분리해 추적한다 — 모니터 프로세스가
// 게임 리스너(7777)의 인증 게이트를 거치지 않고도 별도 포트(7779)로 구독할 수 있게 하기 위함이다.
var telemetryRegistry = ServerNet.CreateSessionRegistry();

// ServerNet.CreateListener(registry): 위에서 만든 레지스트리를 그대로 전달 — 리스너가 접속/해제
// 시 세션을 자동 등록/해제해 registry.BroadcastAsync의 대상 모집단을 채운다. 객체 생성만 여기서
// 먼저 하고(TelemetryPublisher가 ActiveSessionCount 등을 읽으려면 인스턴스가 필요) 실제
// listener.Start()는 raidRunner의 루프들이 기동된 뒤로 미룬다(아래 raidRunner.Start() 주변 주석 참고).
// 멀티포트(용량 모드): P개 리스너 인스턴스가 모두 같은 registry를 공유한다 — IServerListener.Start는
// 인스턴스당 단일 바인드이므로 포트마다 인스턴스가 필요하고, 레지스트리를 공유하면 registry.Count가
// 전 포트의 전역 접속 수가 된다.
var gameListeners = new IServerListener[gamePortCount];
for (int p = 0; p < gamePortCount; p++)
    gameListeners[p] = ServerNet.CreateListener(registry);

// 텔레메트리 리스너: 게임 리스너와 별개의 IServerListener 인스턴스. OnReceived를 배선하지 않으므로
// 모니터 프로세스가 무언가를 보내도 조용히 무시된다(읽기 전용 구독 전용 — 게임 프로토콜과 무관).
IServerListener telemetryListener = ServerNet.CreateListener(telemetryRegistry);
Console.WriteLine("[초기화] 세션 레지스트리 2개 + 리스너 2개 생성 완료(아직 포트를 열지는 않음).");

// TelemetryPublisher: RaidEncounter의 onStep을 SessionRaidRunner가 RaidBroadcaster와 함께 팬아웃
// 구독하도록 아래 raidRunner 생성자에 주입한다. 게임 리스너들(gameListeners)의 ActiveSessionCount/IsRunning/
// TotalRejectedConnections를 합산해 읽고, telemetryRegistry로 접속한 모니터 전원에게 브로드캐스트한다.
var telemetryPublisher = new TelemetryPublisher(gameListeners, telemetryRegistry);

// SessionRaidRunner: binder가 부착한 Player를 읽어 시작 장비를 착용시키고, 공유 레이드 보스(7001)에
// 대한 세션별 피해 제출 루프를 시작한다. 보스 HP 변경·기여도 판정은 내부 RaidEncounter 액터 루프
// 하나가 전담하며, 그 결과를 registry.BroadcastAsync로 전 세션에 푸시한다. telemetryPublisher를
// 함께 주입해 같은 onStep 스트림을 웹 모니터링 대시보드로도 팬아웃한다.
var raidRunner = new SessionRaidRunner(levelSystem, monsterTable, equipmentTable, sink, registry, raidTimeLimit,
    telemetryPublisher: telemetryPublisher);
Console.WriteLine("[초기화] 텔레메트리 퍼블리셔 + 공유 레이드 러너(SessionRaidRunner) 생성 완료.");

// CancellationTokenSource: Ctrl+C(SIGINT) 기본 동작인 즉시 프로세스 종료 대신, 협조적 취소로
// 바꾼다 — sink는 await using으로 선언돼 있어 프로세스가 강제 종료되면 NDJSON 파일의 마지막
// 라인들이 flush되지 못한 채 유실될 수 있다. 기존 스레드 샤딩 데모의 종료 패턴을 그대로 유지.
// SessionRaidRunner.Start의 수명 토큰으로도 그대로 넘긴다(세션별 CTS와는 무관 — 링크하지 않음,
// SessionRaidRunner.cs 클래스 주석 참고) — 레이드 액터 루프·보상 드레인 루프, 이 둘만 이 토큰을 직접 받는다.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // 기본 강제 종료를 막고, 대신 취소 토큰으로 정리할 시간을 준다
    cts.Cancel();
};
// PosixSignalRegistration(SIGTERM): docker compose down/stop이 보내는 신호는 SIGINT가 아니라
// SIGTERM이라 위 CancelKeyPress만으로는 잡히지 않는다 — 등록이 없으면 stop-timeout(기본 10초)을
// 다 채운 뒤 SIGKILL로 강제 종료되어 sink(NDJSON) 마지막 flush가 유실된다. Windows에서는 SIGTERM
// 자체가 발생하지 않아 로컬 dotnet run에는 영향 없다(무해한 등록만 유지).
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    cts.Cancel();
});

// 레이드 액터 루프 + 보상 드레인 루프 + 브로드캐스트 드레인 루프 + 텔레메트리 퍼블리시 루프를 먼저
// 기동한 뒤 리스너를 연다 — 첫 접속이 들어오기 전에 SubmitDamage를 받을 준비가 되어 있어야 한다.
// 용량 모드에서는 레이드 러너를 아예 기동하지 않는다: 참가자가 0명이어도 60초 제한시간 리셋마다
// RaidFailed 스텝이 registry.BroadcastAsync로 전 세션에 O(N) 팬아웃되어 대규모 연결에서 붕괴하기
// 때문. 다만 텔레메트리 퍼블리시 루프는 TelemetryPublisher.PublishLoopAsync를 직접 기동해 살려둔다
// (평소엔 raidRunner.Start 내부에서 띄우지만, 여기선 러너를 안 켜므로 직접 호출).
if (capacityMode)
{
    // Task.Run(fire-and-forget): 퍼블리시 루프는 cts 취소 시 스스로 정리된다(PublishLoopAsync는
    // PeriodicTimer 기반 무한 루프라 호출 스레드를 점유하면 안 됨 — SessionRaidRunner.Start와 동일 패턴).
    _ = Task.Run(() => telemetryPublisher.PublishLoopAsync(cts.Token), cts.Token);
    Console.WriteLine("[가동] CAPACITY MODE: 레이드 러너 미기동(연결+인증+유지만). 텔레메트리 퍼블리시 루프만 직접 기동.");
}
else
{
    raidRunner.Start(cts.Token);
    Console.WriteLine("[초기화] 레이드 액터 루프 + 보상/브로드캐스트 드레인 루프 + 텔레메트리 퍼블리시 루프 기동 완료.");
}

// SessionSendTimeout: 코드리뷰 발견(docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md,
// 보안 Medium/CWE-400) 수정 — 미설정(기본 null=무한 대기) 상태였다면 수신 버퍼를 비우지 않는
// 정지된 피어 하나가 registry.BroadcastAsync를 영원히 붙잡을 수 있었다(SessionRaidRunner의
// 브로드캐스트 드레인 태스크가 그 피어에서 멈춤). 유한값으로 설정해 시한 초과 시 그 세션의 송신만
// SocketException으로 끊고 나머지 브로드캐스트는 계속되게 한다. Start() 호출 전에 설정해야 한다
// (IServerListener.SessionSendTimeout은 Not thread-safe, 이미 수락된 세션에는 소급 적용 안 됨).
// 공유 핸들러를 지역 델리게이트로 hoist해 P개 게임 리스너 전부에 동일하게 배선한다.
// 용량 모드에서는 인증 성공 후 raidRunner.OnConnected를 잇지 않는다(연결+인증+유지만) —
// 레이드 참전이 없으니 O(N) 브로드캐스트 대상에서 게임플레이 팬아웃이 발생하지 않는다.
Func<ISession, ReadOnlyMemory<byte>, ValueTask> onReceived = capacityMode
    ? async (session, data) => { _ = await authGate.HandleAsync(session, data); }
    : async (session, data) =>
    {
        bool justAuthenticated = await authGate.HandleAsync(session, data);
        if (justAuthenticated)
            await raidRunner.OnConnected(session);
    };
// 해제: 평소엔 raidRunner가 먼저 제출 루프를 정지시킨 뒤 binder가 기록한다. 용량 모드에선 레이드가
// 없으므로 binder.OnDisconnected만 호출한다(인증 전 끊긴 세션은 두 경로 모두 방어적 no-op).
Func<ISession, ValueTask> onDisconnected = capacityMode
    ? session => binder.OnDisconnected(session)
    : async session =>
    {
        await raidRunner.OnDisconnected(session);
        await binder.OnDisconnected(session);
    };

foreach (var l in gameListeners)
{
    // SessionSendTimeout: 정지된 피어 하나가 registry.BroadcastAsync를 무한정 붙잡는 것을 막는다
    // (Start() 호출 전에 설정해야 함 — Not thread-safe, 소급 적용 안 됨). 코드리뷰 Medium/CWE-400 수정.
    l.SessionSendTimeout = TimeSpan.FromSeconds(2);
    l.OnReceived = onReceived;
    l.OnClientDisconnected = onDisconnected;
    l.OnClientError = binder.OnError;

    // 하드닝(opt-in): IdleTimeout·MaxConnections도 Start() 전에 설정해야 한다.
    if (idleTimeoutSeconds is int idle)
    {
        l.IdleTimeout = TimeSpan.FromSeconds(idle);
        // OnIdleTimeout: 스윕된 세션은 리스너가 이어서 DisposeAsync로 하드 종료한다(콜백은 관측용).
        // Interlocked: 스윕 루프가 병렬(MaxDegreeOfParallelism=4)이라 원자 증가 필요.
        l.OnIdleTimeout = _ => { Interlocked.Increment(ref idleEvictions); return ValueTask.CompletedTask; };
    }
    if (maxConnections is int max)
        l.MaxConnections = max;
    if (maxFramesPerSecond is int fps)
        l.SessionMaxFramesPerSecond = fps;
}
if (idleTimeoutSeconds is not null || maxConnections is not null || maxFramesPerSecond is not null)
{
    Console.WriteLine($"[가동] 하드닝 활성: " +
        (idleTimeoutSeconds is not null ? $"IdleTimeout={idleTimeoutSeconds}s " : "") +
        (maxConnections is not null ? $"MaxConnections={maxConnections} " : "") +
        (maxFramesPerSecond is not null ? $"MaxFramesPerSecond={maxFramesPerSecond}" : ""));
}
// 텔레메트리 리스너도 같은 근거로 유한 송신 타임아웃을 둔다 — 정지된 모니터 클라이언트 1개가
// 텔레메트리 브로드캐스트 전체를 무한정 붙잡는 것을 막는다.
telemetryListener.SessionSendTimeout = TimeSpan.FromSeconds(2);

// 텔레메트리 리스너는 OnReceived/OnClientConnected/OnClientDisconnected를 배선하지 않는다 —
// telemetryRegistry가 리스너 내부에서 접속/해제를 자동 등록/해제하므로(ServerNet.CreateListener
// 계약) 그 자체로 BroadcastAsync의 대상 모집단이 채워진다. 모니터 프로세스는 아무것도 보내지
// 않는 읽기 전용 구독자이므로 게임 프로토콜을 처리할 필요가 없다.

// IdleTimeout은 설정하지 않는다: 하트비트/핑 프로토콜이 아직 없어, 설정하면 정상 연결도 유휴로
// 오판해 즉시 스윕된다. 미인증 세션은 Player가 없어 리소스 비용이 없으므로 방치해도 안전하다
// (프로토콜이 생기는 사이클에서 타임아웃 정책도 함께 재검토). 텔레메트리 리스너도 동일한 이유로
// IdleTimeout을 두지 않는다 — 모니터는 애초에 아무것도 보내지 않으므로 유휴 판정 자체가 무의미하다.

// bindAddress 기본값은 루프백: 토큰 게이트가 생겼어도 AuthTokenPacket이 여전히 평문으로 오가므로
// (TLS 미도입) IPAddress.Any로 외부에 노출하기엔 이르다. TLS 도입 후 재검토한다. Docker 컨테이너
// 내부망처럼 신뢰 경계가 다른 환경에서는 IDLERPG_GAME_BIND=0.0.0.0으로 명시적으로만 넓힌다.
for (int p = 0; p < gamePortCount; p++)
{
    gameListeners[p].Start(gamePort + p, bindAddress);
    Console.WriteLine($"[가동] 게임 리스너 {p + 1}/{gamePortCount} 시작 -> {bindAddress}:{gamePort + p} (클라이언트 접속 수락 시작)");
}
// 텔레메트리 리스너도 같은 bindAddress를 쓴다 — 인증 게이트가 없는 읽기 전용 리스너라 외부(호스트) 노출은
// 더더욱 이르므로, 컨테이너 환경에서도 docker-compose가 이 포트를 host에 publish하지 않아야 한다.
telemetryListener.Start(telemetryPort, bindAddress);
Console.WriteLine($"[가동] 텔레메트리 리스너 시작 -> {bindAddress}:{telemetryPort} (MonitorServer 구독용, 인증 없음)");

// ConsoleStatusReporter: GameEventSink에 이미 누적된 카운터를 5초 주기로 스냅샷/차분만 하므로
// Record* 호출부(hot path)에는 전혀 관여하지 않는다 — 콘솔 I/O는 이 백그라운드 루프 하나에서만
// 발생한다(클래스 remarks 참고). Fire-and-forget: cts.Token이 취소되면 루프가 스스로 정리된다.
if (consoleStatusIntervalSeconds > 0)
{
    // gameListeners 전체를 넘겨 멀티포트(용량 모드) 시 전 포트 접속 수를 합산한다(TelemetryPublisher와 동일).
    var consoleReporter = new ConsoleStatusReporter(gameListeners, sink, Console.Out,
        interval: TimeSpan.FromSeconds(consoleStatusIntervalSeconds));
    _ = consoleReporter.Start(cts.Token);
    Console.WriteLine($"[가동] 콘솔 상태 리포터 시작 (주기 {consoleStatusIntervalSeconds}s, 유휴 heartbeat 60s).");
}
else
{
    Console.WriteLine("[가동] 콘솔 상태 리포터 비활성화됨(IDLERPG_GAME_CONSOLE_INTERVAL_SECONDS=0).");
}

Console.WriteLine("[가동] GameServer 초기화 완료 - 모든 루프/리스너가 정상 기동되었습니다.");

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C로 정상 종료 진입 — 아래에서 리스너를 멈추고 싱크를 닫아 flush한다.
}

// listener.Stop(): 새 연결 수락 중단 + 활성 세션을 동기적으로 정리한다. 이미 열려 있던 세션의
// OnClientDisconnected는 수신 루프의 finally에서 비동기 발화하므로 Stop() 반환 이후에 실행될 수
// 있다 — 그 경우 아래 sink가 먼저 닫히면 해당 PlayerDisconnected 로그는 best-effort로 유실될 수
// 있다(정상 동작 중 발생하는 연결 해제는 항상 안전하게 기록된다).
foreach (var l in gameListeners)
    l.Stop();
telemetryListener.Stop();

if (idleTimeoutSeconds is not null)
    Console.WriteLine($"[종료] 하드닝: 유휴 스윕으로 축출한 세션 총 {Interlocked.Read(ref idleEvictions)}건.");

// await using으로 선언했으므로 sink.DisposeAsync()는 스코프 종료 시 자동 호출된다
// (CompleteWriting → 소비자 종료 대기 → 파일 flush/close → 계측기 해제).
