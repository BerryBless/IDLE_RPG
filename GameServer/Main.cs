// BigNumber는 double 별칭이다. 방치형 특성상 수치가 매우 커질 수 있어 전용 struct 도입 여지를
// 남겨둔다 — 실제 도입은 인플레이션이 double 정밀도(약 15~17자리)를 위협하는 시점에 재검토한다.
global using BigNumber = double;

using System.Net;
using GameServer.Items;
using GameServer.Systems;
using ServerLib;
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
// 전투 멀티플레이 2단계(이번 사이클): 접속한 모든 클라이언트가 하나의 공유 레이드 보스(몬스터
// 7001)를 동시에 공격한다(SessionRaidRunner) — 보스 HP/처치를 ISessionRegistry로 전 세션에
// 브로드캐스트한다. 보스는 반격하지 않으므로(Atk=0) 플레이어는 죽지 않는다(순수 DPS 레이스).
// 제한시간 내 미처치 시 보상 없이 리셋(RaidFailed). 로그인은 여전히 없다(SessionPlayerBinder가
// 소켓 연결마다 임시 Player를 생성). PvP·실제 로그인·스테이지 시스템은 이후 사이클 과제다.

const int Port = 7777; // examples/EchoServer(9000)와 겹치지 않도록 구분 — 필요 시 동시 실행 가능.
var raidTimeLimit = TimeSpan.FromSeconds(60); // 이 시간 내에 전원이 힘을 모아 보스를 잡아야 한다.

var levelSystem = PlayerLevelSystem.CreateDefault();
var monsterTable = MonsterTable.CreateDefault();
var equipmentTable = EquipmentTable.CreateDefault();

// GameEventSink: 다수 I/O 스레드(생산자)가 Record*로 메트릭+NDJSON 라인을 밀어넣고, 내부 단일
// 소비자 태스크가 파일에 flush한다. 2026-07-07 관측성 전환 이후 Main.cs는 콘솔에 직접 출력하지
// 않는다 — 이 규칙은 네트워크 서버로 전환한 뒤에도 그대로 유지한다.
await using var sink = GameEventSink.CreateFile(Path.Combine("logs", "game-events.ndjson"));

// SessionPlayerBinder: PlayerFactory.CreateTemp로 임시 Player를 만들어 session.Context에
// 부착하고, 연결/해제/오류를 sink에 기록한다.
var binder = new SessionPlayerBinder(levelSystem, sink);

// ServerNet.CreateSessionRegistry(): 공유 보스 co-op는 보스 HP/처치를 접속한 모든 세션에
// 브로드캐스트해야 하므로(1단계와 달리 특정 세션 하나에게만 보내는 것으로는 부족) 세션 레지스트리가
// 필수다 — 반드시 CreateListener에도 같은 인스턴스를 넘겨야 리스너가 접속/해제를 자동 등록/해제한다.
var registry = ServerNet.CreateSessionRegistry();

// SessionRaidRunner: binder가 부착한 Player를 읽어 시작 장비를 착용시키고, 공유 레이드 보스(7001)에
// 대한 세션별 피해 제출 루프를 시작한다. 보스 HP 변경·기여도 판정은 내부 RaidEncounter 액터 루프
// 하나가 전담하며, 그 결과를 registry.BroadcastAsync로 전 세션에 푸시한다.
var raidRunner = new SessionRaidRunner(levelSystem, monsterTable, equipmentTable, sink, registry, raidTimeLimit);

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

// 레이드 액터 루프 + 보상 드레인 루프를 먼저 기동한 뒤 리스너를 연다 — 첫 접속이 들어오기 전에
// SubmitDamage를 받을 준비가 되어 있어야 한다.
raidRunner.Start(cts.Token);

// ServerNet.CreateListener(registry): 위에서 만든 레지스트리를 그대로 전달 — 리스너가 접속/해제
// 시 세션을 자동 등록/해제해 registry.BroadcastAsync의 대상 모집단을 채운다.
IServerListener listener = ServerNet.CreateListener(registry);

// SessionSendTimeout: 코드리뷰 발견(docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md,
// 보안 Medium/CWE-400) 수정 — 미설정(기본 null=무한 대기) 상태였다면 수신 버퍼를 비우지 않는
// 정지된 피어 하나가 registry.BroadcastAsync를 영원히 붙잡을 수 있었다(SessionRaidRunner의
// 브로드캐스트 드레인 태스크가 그 피어에서 멈춤). 유한값으로 설정해 시한 초과 시 그 세션의 송신만
// SocketException으로 끊고 나머지 브로드캐스트는 계속되게 한다. Start() 호출 전에 설정해야 한다
// (IServerListener.SessionSendTimeout은 Not thread-safe, 이미 수락된 세션에는 소급 적용 안 됨).
listener.SessionSendTimeout = TimeSpan.FromSeconds(2);

// 연결: binder가 먼저 실행되어 Player를 Context에 부착한 뒤에 raidRunner가 그 Player를 읽어
// 장비 착용 + 제출 루프를 시작해야 한다. 해제: raidRunner가 먼저 제출 루프를 정지시킨 뒤 binder가
// 연결 해제를 기록한다. Start() 호출 전에 배선을 마쳐야 한다(이후 설정 시 InvalidOperationException).
listener.OnClientConnected = async session =>
{
    await binder.OnConnected(session);
    await raidRunner.OnConnected(session);
};
listener.OnClientDisconnected = async session =>
{
    await raidRunner.OnDisconnected(session);
    await binder.OnDisconnected(session);
};
listener.OnClientError = binder.OnError;
// OnReceived는 아직 배선하지 않는다 — 전투는 서버 자동 틱이라 클라이언트가 보낼 명령이 없다.

// IdleTimeout은 설정하지 않는다: 하트비트/핑 프로토콜이 아직 없어, 설정하면 정상 연결도 유휴로
// 오판해 즉시 스윕된다. 프로토콜이 생기는 사이클에서 함께 도입한다.

// IPAddress.Loopback: 인증(로그인)이 아직 없는 상태이므로 IPAddress.Any로 외부에 노출하면
// 누구나 임시 플레이어를 무제한으로 생성할 수 있는 서비스가 인터넷에 노출된다. 로그인 구현 후
// 재검토한다.
listener.Start(Port, IPAddress.Loopback);

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

// await using으로 선언했으므로 sink.DisposeAsync()는 스코프 종료 시 자동 호출된다
// (CompleteWriting → 소비자 종료 대기 → 파일 flush/close → 계측기 해제).
