// BigNumber는 double 별칭이다. 방치형 특성상 수치가 매우 커질 수 있어 전용 struct 도입 여지를
// 남겨둔다 — 실제 도입은 인플레이션이 double 정밀도(약 15~17자리)를 위협하는 시점에 재검토한다.
global using BigNumber = double;

using System.Net;
using GameServer.Systems;
using ServerLib;
using ServerLib.Interface;

// 클라-서버 분리 1단계: 이전까지 GameServer는 400명의 가상 플레이어가 스레드 샤딩으로 자동
// 전투하는 콘솔 데모였다(스레드 샤딩 배틀/레이드 코드는 git 이력에 보존, Systems/BattleLoop.cs
// 등 도메인 클래스 자체는 그대로 유지되며 각자의 단위 테스트가 계속 커버한다). 이번 사이클부터는
// 실제 TCP 클라이언트가 접속할 수 있는 네트워크 서버로 전환한다.
//
// 이번 사이클 범위는 "연결↔Player 배선"까지다 — 로그인은 아직 없다. 소켓이 연결될 때마다
// SessionPlayerBinder가 임시 Player를 하나 생성해 그 연결에 부착하고, 해제되면 정리 이벤트만
// 남긴다. 전투 명령 등 실제 게임플레이 프로토콜(OnReceived)은 다음 사이클 과제다.

const int Port = 7777; // examples/EchoServer(9000)와 겹치지 않도록 구분 — 필요 시 동시 실행 가능.

var levelSystem = PlayerLevelSystem.CreateDefault();

// GameEventSink: 다수 I/O 스레드(생산자)가 Record*로 메트릭+NDJSON 라인을 밀어넣고, 내부 단일
// 소비자 태스크가 파일에 flush한다. 2026-07-07 관측성 전환 이후 Main.cs는 콘솔에 직접 출력하지
// 않는다 — 이 규칙은 네트워크 서버로 전환한 뒤에도 그대로 유지한다.
await using var sink = GameEventSink.CreateFile(Path.Combine("logs", "game-events.ndjson"));

// SessionPlayerBinder: ISession을 다루는 유일한 지점. PlayerFactory.CreateTemp로 임시 Player를
// 만들어 session.Context에 부착하고, 연결/해제/오류를 sink에 기록한다.
var binder = new SessionPlayerBinder(levelSystem, sink);

// ServerNet.CreateListener(registry: null): 이번 사이클엔 브로드캐스트/전체 세션 열거가 필요
// 없으므로 세션 레지스트리를 넘기지 않는다(레지스트리 생성·유지 비용 없음).
IServerListener listener = ServerNet.CreateListener();

// 콜백은 메서드 그룹으로 그대로 대입 — Start() 호출 전에 배선을 마쳐야 한다(이후 설정 시
// InvalidOperationException).
listener.OnClientConnected = binder.OnConnected;
listener.OnClientDisconnected = binder.OnDisconnected;
listener.OnClientError = binder.OnError;
// OnReceived는 아직 배선하지 않는다 — 이번 사이클엔 게임플레이 프로토콜이 없다.

// IdleTimeout은 설정하지 않는다: 하트비트/핑 프로토콜이 아직 없어, 설정하면 정상 연결도 유휴로
// 오판해 즉시 스윕된다. 프로토콜이 생기는 사이클에서 함께 도입한다.

// IPAddress.Loopback: 인증(로그인)이 아직 없는 상태이므로 IPAddress.Any로 외부에 노출하면
// 누구나 임시 플레이어를 무제한으로 생성할 수 있는 서비스가 인터넷에 노출된다. 로그인 구현 후
// 재검토한다.
listener.Start(Port, IPAddress.Loopback);

// CancellationTokenSource: Ctrl+C(SIGINT) 기본 동작인 즉시 프로세스 종료 대신, 협조적 취소로
// 바꾼다 — sink는 await using으로 선언돼 있어 프로세스가 강제 종료되면 NDJSON 파일의 마지막
// 라인들이 flush되지 못한 채 유실될 수 있다. 기존 스레드 샤딩 데모의 종료 패턴을 그대로 유지.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // 기본 강제 종료를 막고, 대신 취소 토큰으로 정리할 시간을 준다
    cts.Cancel();
};

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
