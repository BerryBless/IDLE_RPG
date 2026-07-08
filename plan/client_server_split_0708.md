# 클라-서버 분리 1단계: 소켓 연결 시 임시 Player 생성

작성일: 2026-07-08

## 1. 배경 및 목적

`ServerLib`는 이미 IDLE_RPG에 반입되어 `examples/EchoServer`로 동작이 검증된 상태였지만,
실제 게임 도메인(`GameServer`)은 네트워크와 전혀 연결되어 있지 않았다. `GameServer/Main.cs`는
400명의 가상 플레이어가 스레드 샤딩으로 자동 전투하는 콘솔 데모일 뿐, 실제 클라이언트가 접속할
방법이 없었다.

이번 사이클의 목적은 클라이언트-서버를 실제로 분리하는 첫걸음이다: **로그인은 아직 만들지
않고, TCP 소켓이 연결될 때마다 그 연결에 바인딩된 임시 `Player`를 하나 생성한다.** 실제 인증은
다음 사이클 과제로 미룬다. 이번 사이클 범위는 명시적으로 "연결↔Player 배선"까지만이며, 전투
명령 등 게임플레이 프로토콜은 포함하지 않는다.

## 2. 설계 결정

| 항목 | 채택 | 대안 | 사유 |
|------|------|------|------|
| 임시 Player 식별자 | `InstanceId = $"player-{session.SessionId:N}"`, `AccountId = 0`, `Level = 1` | 증가 카운터 기반 | ServerLib가 세션마다 부여하는 `Guid`라 충돌 없이 즉시 재사용 가능 |
| 진입점 구성 | `Main.cs`를 네트워크 서버로 완전 교체 | 별도 `GameServer.Host` 프로젝트 신설 | 기존 400명 샤딩 데모는 git 이력에 보존되어 손실 없음. 도메인 클래스(`BattleLoop` 등) 자체는 유지 |
| 포트 | 7777 | 9000(EchoServer와 동일) | EchoServer와 구분해 동시 실행 가능하게 함 |
| 바인딩 주소 | `IPAddress.Loopback` | `IPAddress.Any` | 인증이 없는 상태이므로 외부에 노출하지 않음 |
| 세션↔Player 연결 방식 | `ISession.Context`(내장 슬롯) + `SessionContextExtensions` | 별도 `ConcurrentDictionary<Guid, Player>` 레지스트리 | ServerLib가 이미 이 문제를 해결해 둠(YAGNI) |
| 연결 수 게이지 | 만들지 않음 | GameServer 자체 카운터 추가 | `IServerListener.ActiveSessionCount`가 이미 제공하며, `OnDisconnected` 콜백의 비동기 타이밍 차이로 자체 카운터는 어긋날 수 있음(`SocketPipelineListener.cs` 소스 확인) |
| 연결 로직 위치 | 신규 `SessionPlayerBinder` 클래스로 추출 | `Main.cs`에 인라인(EchoServer 스타일) | `Main.cs`는 top-level 문이라 테스트 불가. `ISession`을 다루는 지점을 한 곳으로 모아 실소켓 통합 테스트로 검증 |
| `PlayerFactory` 확장 방식 | `CreateTemp(Guid sessionId, ...)` 오버로드 추가(ISession 미의존) | `PlayerFactory`가 `ISession`을 직접 받음 | GameServer 도메인 계층이 ServerLib에 의존하지 않도록 유지 — 단위 테스트가 소켓 없이 가능 |

### 검증된 ServerLib 동작 (소스 직접 확인)

- `OnClientConnected`는 세션 등록·`Connected` 전이·`StartReceiving()` 이후에 발화한다.
- `OnClientError`는 `OnClientDisconnected`보다 항상 먼저 실행된다(수신 루프 예외 시).
- `OnClientDisconnected` 콜백 내부에서 `session.DisposeAsync()`보다 먼저 호출되므로, 이 콜백
  안에서는 `session.Context`가 아직 유효하다(`DisposeAsync` 안에서만 null화).
- `Stop()`은 활성 세션을 동기적으로 정리하지만, 이미 열려 있던 세션의 `OnClientDisconnected`는
  수신 루프의 `finally`에서 비동기 실행되어 `Stop()` 반환 이후에 발화할 수 있다 — 따라서 프로세스
  종료 시점에 걸린 연결의 `PlayerDisconnected` 로그는 best-effort이며 유실될 수 있다(정상 동작
  중 발생하는 연결 해제는 항상 안전하게 기록됨).
- `OnReceived`가 배선되지 않은 세션은 PING이 아닌 패킷을 그냥 무시하고 `ValueTask.CompletedTask`를
  반환한다(`SocketPipelineSession.DispatchPacketAsync`) — 즉 이번 사이클에는 `OnClientError`가
  사실상 도달하지 않는 경로다. 콜백 계약은 온전히 구현해 두어(`SessionPlayerBinder.OnError`)
  다음 사이클에 `OnReceived`가 추가되는 즉시 자동으로 살아나게 했다.

## 3. 컴포넌트 구조

```
GameServer/
├─ Main.cs                          (전체 교체 — 400명 샤딩 데모 제거, 네트워크 서버 진입점)
├─ Systems/
│  ├─ PlayerFactory.cs              (CreateTemp(Guid, PlayerLevelSystem) 오버로드 추가)
│  ├─ GameEventSink.cs              (PlayerConnected/Disconnected/ConnectionError 3종 추가)
│  ├─ GameMetrics.cs                (대응 Counter<long> 3종 추가)
│  └─ SessionPlayerBinder.cs        (신규 — ISession을 다루는 유일한 GameServer 타입)
├─ GameServer.csproj                (ServerLib ProjectReference 추가)
└─ (Entities/Items/Combat/Stats/기타 Systems — 변경 없음, 기존 도메인 클래스·테스트 그대로 유지)

tests/GameServer.Tests/
├─ GameServer.Tests.csproj                          (ServerLib ProjectReference 추가)
└─ Systems/
   ├─ PlayerFactoryTests.cs                          (CreateTemp 케이스 추가)
   ├─ GameEventSinkTests.cs                          (새 포맷터/Record 케이스 추가)
   ├─ GameMetricsTests.cs                             (새 카운터 케이스 추가)
   └─ SessionConnectionEndToEndTests.cs               (신규 — 실 루프백 소켓 통합 테스트)
```

의존 관계:
```
ServerLib (루트 라이브러리)
   ↑ ProjectReference
   ├─ GameServer            (신규: Main.cs → SessionPlayerBinder → ISession)
   └─ tests/GameServer.Tests (신규: 실소켓 통합 테스트가 ServerNet.CreateClient() 직접 사용)

GameServer.Systems.PlayerFactory / GameEventSink / GameMetrics
   — ServerLib를 전혀 참조하지 않음(Guid만 받음) → 소켓 없이 단위 테스트 가능
```

## 4. 핵심 API

```csharp
// PlayerFactory: 세션 Guid만으로 임시 플레이어 생성 (ISession 비의존)
Player player = PlayerFactory.CreateTemp(session.SessionId, levelSystem);

// SessionPlayerBinder: IServerListener 콜백에 메서드 그룹으로 그대로 배선
var binder = new SessionPlayerBinder(levelSystem, sink);
listener.OnClientConnected    = binder.OnConnected;
listener.OnClientDisconnected = binder.OnDisconnected;
listener.OnClientError        = binder.OnError;
listener.Start(7777, IPAddress.Loopback);

// 연결 해제 시 조회 (세션 Context는 OnDisconnected 안에서 아직 유효)
if (session.TryGetContext<Player>(out var player))
    sink.RecordPlayerDisconnected(player.InstanceId);
```

## 5. 변경 파일 목록

| 파일 | 구분 | 내용 |
|------|------|------|
| `GameServer/Systems/PlayerFactory.cs` | 수정 | `CreateTemp(Guid, PlayerLevelSystem)` 오버로드 추가 |
| `GameServer/Systems/GameEventSink.cs` | 수정 | `PlayerConnected`/`PlayerDisconnected`/`PlayerConnectionError` 포맷터+Record 메서드 3종 추가 |
| `GameServer/Systems/GameMetrics.cs` | 수정 | 대응 `Counter<long>` 3종 추가 |
| `GameServer/Systems/SessionPlayerBinder.cs` | 신규 | 연결↔Player 바인딩 전담 클래스 |
| `GameServer/Main.cs` | 전체 교체 | 400명 샤딩 데모 제거, `ServerNet` 기반 네트워크 서버 진입점(포트 7777) |
| `GameServer/GameServer.csproj` | 수정 | `ServerLib` ProjectReference 추가 |
| `tests/GameServer.Tests/GameServer.Tests.csproj` | 수정 | `ServerLib` ProjectReference 추가 |
| `tests/GameServer.Tests/Systems/PlayerFactoryTests.cs` | 수정 | `CreateTemp` 단위 테스트 4건 추가 |
| `tests/GameServer.Tests/Systems/GameEventSinkTests.cs` | 수정 | 새 포맷터/Record 테스트 4건 추가 |
| `tests/GameServer.Tests/Systems/GameMetricsTests.cs` | 수정 | 새 카운터 케이스 추가(기존 단일 테스트 확장) |
| `tests/GameServer.Tests/Systems/SessionConnectionEndToEndTests.cs` | 신규 | 실 루프백 소켓으로 연결→해제 전체 사이클 검증 |

## 6. 빌드 검증

```powershell
dotnet build IDLE_RPG.sln
dotnet test tests/GameServer.Tests/GameServer.Tests.csproj
```

**검증 결과(2026-07-08):** 빌드 0 에러(경고 10개는 ServerLib 반입 시점부터 있던 기존 CS0419,
이번 변경과 무관) / `GameServer.Tests` 127/127 통과(신규 통합 테스트 5회 연속 재실행으로 플레이키
없음 확인) / `EchoExample.Tests` 13/13 통과(회귀 없음).

실제 런타임 스모크 테스트: `dotnet run --project GameServer`로 서버를 7777에 기동한 뒤 실제 raw
TCP 소켓으로 연결·해제해 `logs/game-events.ndjson`에 다음 두 줄이 동일 `playerId`로 기록됨을
확인 — `session.Context`가 `OnConnected`→`OnDisconnected` 사이에서 올바르게 왕복함을 실제
네트워크 위에서 실증:
```json
{"ts":"2026-07-08T05:46:38Z","type":"PlayerConnected","playerId":"player-d154f0ab62b24bab9f24f658d7337ccf","level":1}
{"ts":"2026-07-08T05:46:39Z","type":"PlayerDisconnected","playerId":"player-d154f0ab62b24bab9f24f658d7337ccf"}
```
프로세스 종료(Ctrl+C 상당) 후 잔여 프로세스·포트 점유 없음도 확인.

## 7. 향후 확장 포인트

- **실제 로그인 구현:** `AccountId=0` 플레이스홀더를 인증된 계정 ID로 교체. `ServerLib`에 이미
  존재하는 `LoginRequestPacket`/`LoginResponsePacket`/`AuthTokenPacket`을 활용할 수 있다.
- **게임플레이 프로토콜 도입:** `listener.OnReceived` 배선 및 전투/스탯 조회 등 패킷 설계.
  이때 `SessionPlayerBinder.OnError` 경로가 처음으로 실제 도달 가능해지므로 그 시점에 전용
  테스트를 추가한다.
- **하트비트/IdleTimeout:** 현재 `IdleTimeout` 미설정 상태 — 핑/퐁 프로토콜 도입 시 함께 설정.
- **다중 세션 조회:** 브로드캐스트나 전체 플레이어 열거가 필요해지면 `ServerNet.CreateSessionRegistry()`를
  `CreateListener`에 전달.
