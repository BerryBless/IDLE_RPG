// BigNumber는 double 별칭이다. 방치형 특성상 수치가 매우 커질 수 있어 전용 struct 도입 여지를
// 남겨둔다 — 실제 도입은 인플레이션이 double 정밀도(약 15~17자리)를 위협하는 시점에 재검토한다.
global using BigNumber = double;

using System.Net;
using System.Text;
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

const int Port = 7777; // examples/EchoServer(9000)와 겹치지 않도록 구분 — 필요 시 동시 실행 가능.
const int TelemetryPort = 7779; // 게임 리스너(7777)와 겹치지 않는 별도 포트 — 웹 모니터링 전용 읽기 전용 구독.
var raidTimeLimit = TimeSpan.FromSeconds(60); // 이 시간 내에 전원이 힘을 모아 보스를 잡아야 한다.

var levelSystem = PlayerLevelSystem.CreateDefault();
var monsterTable = MonsterTable.CreateDefault();
var equipmentTable = EquipmentTable.CreateDefault();

// GameEventSink: 다수 I/O 스레드(생산자)가 Record*로 메트릭+NDJSON 라인을 밀어넣고, 내부 단일
// 소비자 태스크가 파일에 flush한다. 2026-07-07 관측성 전환 이후 Main.cs는 콘솔에 직접 출력하지
// 않는다 — 이 규칙은 네트워크 서버로 전환한 뒤에도 그대로 유지한다.
await using var sink = GameEventSink.CreateFile(Path.Combine("logs", "game-events.ndjson"));

// SessionPlayerBinder: 이제 OnConnected는 배선하지 않는다(SessionAuthGate가 대체) — 연결
// 해제/오류 시 sink에 기록하는 OnDisconnected/OnError만 계속 사용한다.
var binder = new SessionPlayerBinder(levelSystem, sink);

// HMAC 시크릿: AuthServer(AuthServerConfig.HmacSecret)와 반드시 동일한 값을 공유해야 발급된
// 토큰을 이쪽에서 검증할 수 있다. 설정값이 이거 하나뿐이라 Port처럼 인라인으로 읽는다(별도
// Config 클래스 없음).
var hmacSecret = Encoding.UTF8.GetBytes(
    Environment.GetEnvironmentVariable("IDLERPG_AUTH_HMAC_SECRET") ?? "dev-only-insecure-hmac-secret-change-me");
// HmacAuthTokenCodec: 발급(IAuthTokenIssuer)도 구현하지만 GameServer는 검증(IAuthTokenValidator)만
// 쓴다 — 토큰 발급은 AuthServer 전용 책임.
var authGate = new SessionAuthGate(new HmacAuthTokenCodec(hmacSecret), levelSystem, sink, new BinaryPacketSerializer());

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
IServerListener listener = ServerNet.CreateListener(registry);

// 텔레메트리 리스너: 게임 리스너와 별개의 IServerListener 인스턴스. OnReceived를 배선하지 않으므로
// 모니터 프로세스가 무언가를 보내도 조용히 무시된다(읽기 전용 구독 전용 — 게임 프로토콜과 무관).
IServerListener telemetryListener = ServerNet.CreateListener(telemetryRegistry);

// TelemetryPublisher: RaidEncounter의 onStep을 SessionRaidRunner가 RaidBroadcaster와 함께 팬아웃
// 구독하도록 아래 raidRunner 생성자에 주입한다. 게임 리스너(listener)의 ActiveSessionCount/IsRunning/
// TotalRejectedConnections를 읽고, telemetryRegistry로 접속한 모니터 전원에게 브로드캐스트한다.
var telemetryPublisher = new TelemetryPublisher(listener, telemetryRegistry);

// SessionRaidRunner: binder가 부착한 Player를 읽어 시작 장비를 착용시키고, 공유 레이드 보스(7001)에
// 대한 세션별 피해 제출 루프를 시작한다. 보스 HP 변경·기여도 판정은 내부 RaidEncounter 액터 루프
// 하나가 전담하며, 그 결과를 registry.BroadcastAsync로 전 세션에 푸시한다. telemetryPublisher를
// 함께 주입해 같은 onStep 스트림을 웹 모니터링 대시보드로도 팬아웃한다.
var raidRunner = new SessionRaidRunner(levelSystem, monsterTable, equipmentTable, sink, registry, raidTimeLimit,
    telemetryPublisher: telemetryPublisher);

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

// 레이드 액터 루프 + 보상 드레인 루프 + 브로드캐스트 드레인 루프 + 텔레메트리 퍼블리시 루프를 먼저
// 기동한 뒤 리스너를 연다 — 첫 접속이 들어오기 전에 SubmitDamage를 받을 준비가 되어 있어야 한다.
raidRunner.Start(cts.Token);

// SessionSendTimeout: 코드리뷰 발견(docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md,
// 보안 Medium/CWE-400) 수정 — 미설정(기본 null=무한 대기) 상태였다면 수신 버퍼를 비우지 않는
// 정지된 피어 하나가 registry.BroadcastAsync를 영원히 붙잡을 수 있었다(SessionRaidRunner의
// 브로드캐스트 드레인 태스크가 그 피어에서 멈춤). 유한값으로 설정해 시한 초과 시 그 세션의 송신만
// SocketException으로 끊고 나머지 브로드캐스트는 계속되게 한다. Start() 호출 전에 설정해야 한다
// (IServerListener.SessionSendTimeout은 Not thread-safe, 이미 수락된 세션에는 소급 적용 안 됨).
listener.SessionSendTimeout = TimeSpan.FromSeconds(2);
// 텔레메트리 리스너도 같은 근거로 유한 송신 타임아웃을 둔다 — TelemetryPublisher.BroadcastPacketAsync도
// telemetryRegistry.BroadcastAsync를 거치므로, 정지된 모니터 클라이언트 1개가 텔레메트리 브로드캐스트
// 전체를 무한정 붙잡는 것을 막는다.
telemetryListener.SessionSendTimeout = TimeSpan.FromSeconds(2);

// 연결 자체로는 아무것도 하지 않는다(OnClientConnected 미배선) — Player는 인증 성공 전까지
// 만들어지지 않는다. 클라이언트가 AuthTokenPacket을 보내야 비로소 SessionAuthGate가 검증하고,
// 이번 호출에서 "새로 인증 성공"(반환값 true)한 경우에만 raidRunner.OnConnected로 이어 레이드
// 참전을 시작한다. authGate.HandleAsync는 IServerListener.OnReceived와 시그니처가 달라(bool 반환)
// 직접 대입할 수 없으므로 이 얇은 람다로 감싼다.
listener.OnReceived = async (session, data) =>
{
    bool justAuthenticated = await authGate.HandleAsync(session, data);
    if (justAuthenticated)
        await raidRunner.OnConnected(session);
};
// 해제: raidRunner가 먼저 제출 루프를 정지시킨 뒤 binder가 연결 해제를 기록한다. 인증 전에
// 끊긴 세션은 TryGetContext<Player> 실패로 두 메서드 모두 방어적으로 no-op 처리된다.
listener.OnClientDisconnected = async session =>
{
    await raidRunner.OnDisconnected(session);
    await binder.OnDisconnected(session);
};
listener.OnClientError = binder.OnError;

// 텔레메트리 리스너는 OnReceived/OnClientConnected/OnClientDisconnected를 배선하지 않는다 —
// telemetryRegistry가 리스너 내부에서 접속/해제를 자동 등록/해제하므로(ServerNet.CreateListener
// 계약) 그 자체로 BroadcastAsync의 대상 모집단이 채워진다. 모니터 프로세스는 아무것도 보내지
// 않는 읽기 전용 구독자이므로 게임 프로토콜을 처리할 필요가 없다.

// IdleTimeout은 설정하지 않는다: 하트비트/핑 프로토콜이 아직 없어, 설정하면 정상 연결도 유휴로
// 오판해 즉시 스윕된다. 미인증 세션은 Player가 없어 리소스 비용이 없으므로 방치해도 안전하다
// (프로토콜이 생기는 사이클에서 타임아웃 정책도 함께 재검토). 텔레메트리 리스너도 동일한 이유로
// IdleTimeout을 두지 않는다 — 모니터는 애초에 아무것도 보내지 않으므로 유휴 판정 자체가 무의미하다.

// IPAddress.Loopback: 토큰 게이트가 생겼어도 AuthTokenPacket이 여전히 평문으로 오가므로(TLS
// 미도입) IPAddress.Any로 외부에 노출하기엔 이르다. TLS 도입 후 재검토한다.
listener.Start(Port, IPAddress.Loopback);
// 텔레메트리 리스너도 루프백 한정 — 인증 게이트가 없는 읽기 전용 리스너라 외부 노출은 더더욱 이르다.
telemetryListener.Start(TelemetryPort, IPAddress.Loopback);

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
listener.Stop();
telemetryListener.Stop();

// await using으로 선언했으므로 sink.DisposeAsync()는 스코프 종료 시 자동 호출된다
// (CompleteWriting → 소비자 종료 대기 → 파일 flush/close → 계측기 해제).
