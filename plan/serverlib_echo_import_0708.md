# ServerLib 반입 및 에코 서버 검증

작성일: 2026-07-08

## 1. 배경 및 목적

`IDLE_RPG`는 아직 게임 도메인(`GameServer`)만 갖추고 있고, 클라이언트-서버 통신을 담당할
네트워크 레이어가 없다. 별도 프로젝트 `ClaudeCodeStudy`에 이미 완성되어 검증된 고성능
.NET 10 비동기 소켓 서버 라이브러리 `ServerLib`(System.IO.Pipelines 기반 Zero-copy
송수신, 세션 레지스트리, 하트비트, RUDP 등 포함)가 존재한다.

이번 작업의 목적은 `ServerLib`를 `IDLE_RPG` 저장소로 반입하여 향후 `GameServer`가 사용할
네트워킹 기반을 마련하고, 반입이 정상적으로 동작하는지를 **에코 서버/클라이언트 예제만으로
1차 검증**하는 것이다. `GameServer`와의 실제 통합(패킷 프로토콜 설계 등)은 다음 사이클
과제로 남긴다.

## 2. 설계 결정

| 항목 | 채택 | 대안 | 사유 |
|------|------|------|------|
| 반입 방식 | **소스 복사(독립 소유)** | 로컬 NuGet 패키지 참조 / 크로스 레포 ProjectReference | IDLE_RPG는 별도 git 저장소이므로 경로 의존이나 반복 pack 작업 없이 완전히 자기 완결적으로 유지. 이후 게임 요구사항에 맞춰 자유롭게 수정 가능 |
| 이름공간 | **`ServerLib` 그대로 유지** | `IdleRpg.ServerLib`로 변경 | 30여 개 파일의 namespace 일괄 변경 없이 최소 변경으로 반입. 추후 ClaudeCodeStudy 개선사항을 수동 병합할 때도 diff가 단순해짐 |
| ServerLib 배치 위치 | **저장소 루트 (`GameServer`와 동급)** | `examples/` 하위 | `ServerLib`는 예제가 아니라 향후 `GameServer`가 직접 참조할 핵심 네트워킹 라이브러리이므로 루트에 둔다 |
| EchoServer/EchoClient 배치 위치 | **`examples/` 하위** | 루트 직속 | 게임 도메인(`GameServer`)과 외부 라이브러리 사용 예제를 시각적으로 분리 |
| 자동 테스트 이식 | **포함 (`EchoExample.Tests`)** | 제외, 수동 실행만 | 이미 검증된 xUnit 테스트(End-to-End, 패킷 라운드트립)를 함께 가져오면 반복 가능한 회귀 검증이 생기고 추가 비용이 거의 없음 |
| 테스트 패키지 버전 | **IDLE_RPG 기존 컨벤션에 맞춤**(`Microsoft.NET.Test.Sdk 17.14.1`, `xunit.runner.visualstudio 3.1.4`, `coverlet.collector 6.0.4`) | ClaudeCodeStudy 원본 버전 유지 | 저장소 내 다른 테스트 프로젝트(`GameServer.Tests` 등)와 버전 일관성 유지 |

## 3. 컴포넌트 구조

```
IDLE_RPG/
├─ GameServer/                          (기존)
├─ ServerLib/                            ← 신규 (소스 복사, 무수정)
│   ├─ Core/
│   │   ├─ Memory/PacketPool.cs
│   │   ├─ Rpc/RpcDispatcher.cs
│   │   ├─ Rudp/{RudpChannel,RudpRecvWindow,RudpSendQueue}.cs
│   │   ├─ Serialization/{BinaryPacketSerializer,IPacket,IPacketSerializer,
│   │   │                 PacketSendExtensions,SpanReader,SpanWriter}.cs
│   │   ├─ Serialization/Packets/
│   │   ├─ ServerMetrics.cs
│   │   ├─ SessionContextExtensions.cs
│   │   ├─ SessionRegistry.cs
│   │   └─ Transport/{HeartbeatProtocol,SocketPipelineClient,
│   │                  SocketPipelineListener,SocketPipelineSession,
│   │                  UdpHolePuncher}.cs
│   ├─ Interface/{IClientConnection,IRpcHandler,IServerListener,
│   │             ISession,ISessionRegistrar,ISessionRegistry,SessionState}.cs
│   ├─ ServerNet.cs                      (공개 팩토리 진입점)
│   └─ ServerLib.csproj
├─ examples/
│   ├─ EchoServer/{EchoServer.csproj, Program.cs}   ← 신규
│   └─ EchoClient/{EchoClient.csproj, Program.cs}   ← 신규
├─ tests/
│   ├─ GameServer.Tests/                 (기존)
│   ├─ IdleRpg.HarnessTests/              (기존)
│   ├─ IdleRpg.Grader/                    (기존)
│   └─ EchoExample.Tests/                 ← 신규
│       ├─ EchoEndToEndTests.cs
│       ├─ EchoPacketRoundTripTests.cs
│       └─ EchoExample.Tests.csproj
├─ TestCode/                              (기존)
└─ IDLE_RPG.sln                           (ServerLib·EchoServer·EchoClient·EchoExample.Tests 등록)
```

의존 관계:
```
GameServer (독립, 아직 ServerLib 미참조)

ServerLib (신규 루트 라이브러리, 외부 의존성: Microsoft.Extensions.ObjectPool)
   ↑ ProjectReference
   ├─ examples/EchoServer
   ├─ examples/EchoClient
   └─ tests/EchoExample.Tests
```

## 4. 핵심 API (변경 없음 — ClaudeCodeStudy 예제 그대로 이식)

```csharp
using ServerLib;
using ServerLib.Interface;

// 서버
IServerListener listener = ServerNet.CreateListener();
listener.OnReceived = (session, data) => session.SendAsync(received);
listener.Start(9000, IPAddress.Loopback);

// 클라이언트
await using IClientConnection client = ServerNet.CreateClient();
client.OnReceived = data => { /* ... */ return ValueTask.CompletedTask; };
await client.ConnectAsync("127.0.0.1", 9000);
await client.SendAsync(new EchoPacket { Message = line });
```

## 5. 변경 파일 목록

| 파일/디렉토리 | 구분 | 내용 |
|------|------|------|
| `ServerLib/**` | 신규(복사) | ClaudeCodeStudy `ServerLib` 전체 소스 무수정 복사 (bin/obj 제외) |
| `examples/EchoServer/**` | 신규(복사+경로수정) | `EchoServer.csproj`의 `ProjectReference`를 `..\..\ServerLib\ServerLib.csproj`로 조정. `Program.cs`는 무수정 |
| `examples/EchoClient/**` | 신규(복사+경로수정) | 위와 동일 |
| `tests/EchoExample.Tests/**` | 신규(복사+경로/버전수정) | `ProjectReference` 경로 조정 + 패키지 버전을 IDLE_RPG 컨벤션에 맞춤. 테스트 로직(`.cs`)은 무수정 |
| `IDLE_RPG.sln` | 수정 | 4개 프로젝트 추가 (`ServerLib`는 최상위, `EchoServer`/`EchoClient`는 신규 `examples` 솔루션 폴더, `EchoExample.Tests`는 기존 `tests` 솔루션 폴더) |
| `CLAUDE.md` | 수정 | "예제 코드 위치" 섹션에 `ServerLib`/`EchoServer`/`EchoClient` 한 줄 요약 추가 |
| `plan/serverlib_echo_import_0708.md` | 신규 | 본 문서 |

## 6. 빌드 검증

```powershell
# 1) 빌드 — 신규 프로젝트 포함 솔루션 전체 빌드
dotnet build IDLE_RPG.sln

# 2) 자동 테스트 — 이식된 EchoExample.Tests(End-to-End + 패킷 라운드트립) 통과 확인
dotnet test tests/EchoExample.Tests

# 3) 수동 에코 왕복 확인
dotnet run --project examples/EchoServer     # 별도 터미널
dotnet run --project examples/EchoClient     # 메시지 입력 → 서버 콘솔에 [수신] 로그 → 클라이언트에 [서버 응답] 로그 확인
```

## 7. 향후 확장 포인트

- `GameServer`가 `ServerLib`를 직접 참조하도록 통합 (패킷 프로토콜을 게임 도메인에 맞게 설계)
- `ServerLib`를 IDLE_RPG 전용으로 발전시키며 필요 없는 기능(RUDP, UDP 홀펀칭 등) 정리 여부 검토
- ClaudeCodeStudy 쪽 `ServerLib` 개선사항 발생 시 수동 cherry-pick 절차 문서화
