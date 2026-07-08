# ServerLib 반입 및 에코 서버 검증 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** ClaudeCodeStudy의 `ServerLib`(고성능 .NET 10 비동기 소켓 서버 라이브러리)를 IDLE_RPG로 소스 복사 반입하고, 에코 서버/클라이언트 예제 + 자동화 테스트로 반입이 정상 동작함을 검증한다.

**Architecture:** `ServerLib`를 저장소 루트에 무수정 복사하여 독립 소유 라이브러리로 만들고, 이를 소비하는 `examples/EchoServer`·`examples/EchoClient`·`tests/EchoExample.Tests`를 순서대로 반입한다. 각 프로젝트는 `dotnet sln add`로 솔루션에 등록하고 즉시 빌드 검증한다. 마지막에 실제 TCP 왕복(자동화 스모크 테스트)으로 런타임 동작을 확인한다.

**Tech Stack:** .NET 10 (net10.0), System.IO.Pipelines 기반 `ServerLib`, xUnit(`EchoExample.Tests`), `Microsoft.Extensions.ObjectPool`.

## Global Constraints

- 대상 프레임워크: `net10.0` (모든 신규 프로젝트 공통, 저장소 기존 프로젝트와 동일)
- `ServerLib`는 ClaudeCodeStudy 원본을 **무수정 복사** — 네임스페이스·프로젝트명·NuGet 패키징 메타데이터(`PackageId`, `GenerateDocumentationFile` 등) 그대로 유지
- 배치 위치: `ServerLib/`(루트, GameServer와 동급), `examples/EchoServer/`, `examples/EchoClient/`, `tests/EchoExample.Tests/`
- `EchoServer.csproj`/`EchoClient.csproj`/`EchoExample.Tests.csproj`의 `ProjectReference`는 `..\..\ServerLib\ServerLib.csproj`로 조정(원본은 `..\ServerLib\...`였으나 한 단계 더 깊이 이동)
- `EchoExample.Tests.csproj` 패키지 버전은 저장소 기존 테스트 프로젝트(`tests/GameServer.Tests/GameServer.Tests.csproj`) 컨벤션에 맞춤: `Microsoft.NET.Test.Sdk 17.14.1`, `xunit 2.9.3`(원본과 동일), `xunit.runner.visualstudio 3.1.4`, `coverlet.collector 6.0.4` 추가, `<Using Include="Xunit" />` 추가
- 솔루션 등록은 `dotnet sln IDLE_RPG.sln add <경로> [--in-root | -s <폴더>]` 사용 (수동 GUID 편집 금지)
- 원본 소스 위치: `E:\project\ClaudeCodeStudy\ServerLib`, `E:\project\ClaudeCodeStudy\EchoServer`, `E:\project\ClaudeCodeStudy\EchoClient`, `E:\project\ClaudeCodeStudy\EchoExample.Tests` (Bash 도구 기준 `/e/project/ClaudeCodeStudy/...`)
- 작업 디렉터리: `E:\project\IDLE_RPG` (Bash 도구 기준 `/e/project/IDLE_RPG`)
- 커밋 메시지는 한국어, `{접두사}: {제목}` 형식(추가/수정/버그수정/리팩토링/문서/테스트/의존성), 마지막 줄에 `Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>`
- 설계 근거 문서: `plan/serverlib_echo_import_0708.md` (이미 작성·커밋됨)

---

### Task 1: ServerLib 소스 반입 및 솔루션 등록

**Files:**
- Create: `ServerLib/ServerLib.csproj` (원본 무수정 복사)
- Create: `ServerLib/ServerNet.cs` (원본 무수정 복사)
- Create: `ServerLib/Core/**` (원본 `Core/` 서브트리 무수정 복사, 39개 `.cs` 파일)
- Create: `ServerLib/Interface/**` (원본 `Interface/` 서브트리 무수정 복사, 7개 `.cs` 파일)
- Modify: `IDLE_RPG.sln`

**Interfaces:**
- Produces: `ServerLib` 어셈블리 — `namespace ServerLib` 최상위에 `public static class ServerNet` 노출:
  - `static IServerListener ServerNet.CreateListener(ISessionRegistry? registry = null)`
  - `static IClientConnection ServerNet.CreateClient()`
  - `static ISessionRegistry ServerNet.CreateSessionRegistry()`
  - `namespace ServerLib.Interface`에 `IServerListener`, `IClientConnection`, `ISession`, `ISessionRegistry` 등
  - `namespace ServerLib.Core.Serialization`에 `BinaryPacketSerializer`, `IPacket`
  - `namespace ServerLib.Core.Serialization.Packets`에 `EchoPacket`(및 기타 패킷 타입)
  - `namespace ServerLib.Core.Serialization`에 확장 메서드 `PacketSendExtensions`(`ISession.SendAsync<T>`, `IClientConnection.SendAsync<T>`)
  - 이후 Task 2·3·4가 `ServerLib/ServerLib.csproj`를 `ProjectReference`로 소비한다.

- [ ] **Step 1: ServerLib 디렉터리 생성 및 소스 복사**

```bash
cd /e/project/IDLE_RPG
mkdir -p ServerLib
cp -r "/e/project/ClaudeCodeStudy/ServerLib/Core" ServerLib/Core
cp -r "/e/project/ClaudeCodeStudy/ServerLib/Interface" ServerLib/Interface
cp "/e/project/ClaudeCodeStudy/ServerLib/ServerNet.cs" ServerLib/ServerNet.cs
cp "/e/project/ClaudeCodeStudy/ServerLib/ServerLib.csproj" ServerLib/ServerLib.csproj
```

- [ ] **Step 2: 복사본이 원본과 동일한지 확인**

Run:
```bash
diff -rq /e/project/ClaudeCodeStudy/ServerLib/Core /e/project/IDLE_RPG/ServerLib/Core
diff -rq /e/project/ClaudeCodeStudy/ServerLib/Interface /e/project/IDLE_RPG/ServerLib/Interface
diff /e/project/ClaudeCodeStudy/ServerLib/ServerNet.cs /e/project/IDLE_RPG/ServerLib/ServerNet.cs
diff /e/project/ClaudeCodeStudy/ServerLib/ServerLib.csproj /e/project/IDLE_RPG/ServerLib/ServerLib.csproj
```
Expected: 4개 명령 모두 출력 없음(완전 동일).

- [ ] **Step 3: 솔루션에 등록 (루트)**

Run:
```bash
dotnet sln IDLE_RPG.sln add ServerLib/ServerLib.csproj --in-root
```
Expected: `Project `ServerLib\ServerLib.csproj` added to the solution.` (또는 한국어 동일 메시지)

- [ ] **Step 4: 빌드 검증**

Run:
```bash
dotnet build ServerLib/ServerLib.csproj -c Debug
```
Expected: `Build succeeded.` / 오류 0, 경고 0 (원본이 이미 경고 0으로 검증됨).

- [ ] **Step 5: 커밋**

```bash
git add ServerLib IDLE_RPG.sln
git commit -m "$(cat <<'EOF'
추가: ClaudeCodeStudy ServerLib 소스 반입

향후 GameServer가 사용할 고성능 소켓 서버 라이브러리를 독립 소유
소스로 반입. plan/serverlib_echo_import_0708.md 설계에 따른
1단계 작업.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: EchoServer 예제 반입 및 솔루션 등록

**Files:**
- Create: `examples/EchoServer/EchoServer.csproj`
- Create: `examples/EchoServer/Program.cs` (원본 무수정 복사)
- Modify: `IDLE_RPG.sln`

**Interfaces:**
- Consumes: Task 1의 `ServerLib.ServerNet.CreateListener()`, `ServerLib.Interface.IServerListener`, `ServerLib.Core.Serialization.BinaryPacketSerializer`, `ServerLib.Core.Serialization.Packets.EchoPacket`, `PacketSendExtensions.SendAsync`
- Produces: 포트 9000(`IPAddress.Loopback`)에서 TCP 에코 응답을 수행하는 실행 파일 `examples/EchoServer` — Task 5의 스모크 테스트가 `dotnet run --project examples/EchoServer`로 구동한다.

- [ ] **Step 1: EchoServer 디렉터리 생성 및 소스 복사**

```bash
cd /e/project/IDLE_RPG
mkdir -p examples/EchoServer
cp "/e/project/ClaudeCodeStudy/EchoServer/Program.cs" examples/EchoServer/Program.cs
cp "/e/project/ClaudeCodeStudy/EchoServer/EchoServer.csproj" examples/EchoServer/EchoServer.csproj
```

- [ ] **Step 2: ProjectReference 경로 조정**

`examples/EchoServer/EchoServer.csproj`의 11번째 줄:

Before:
```xml
    <ProjectReference Include="..\ServerLib\ServerLib.csproj" />
```

After:
```xml
    <ProjectReference Include="..\..\ServerLib\ServerLib.csproj" />
```

- [ ] **Step 3: Program.cs가 원본과 동일한지 확인**

Run:
```bash
diff /e/project/ClaudeCodeStudy/EchoServer/Program.cs /e/project/IDLE_RPG/examples/EchoServer/Program.cs
```
Expected: 출력 없음(완전 동일).

- [ ] **Step 4: 솔루션에 등록 (examples 폴더)**

Run:
```bash
dotnet sln IDLE_RPG.sln add examples/EchoServer/EchoServer.csproj -s examples
```
Expected: `Project `examples\EchoServer\EchoServer.csproj` added to the solution.`

- [ ] **Step 5: 빌드 검증**

Run:
```bash
dotnet build examples/EchoServer/EchoServer.csproj -c Debug
```
Expected: `Build succeeded.` / 오류 0.

- [ ] **Step 6: 커밋**

```bash
git add examples/EchoServer IDLE_RPG.sln
git commit -m "$(cat <<'EOF'
추가: ServerLib 검증용 EchoServer 예제 반입

ServerLib 반입(Task 1)이 실제로 동작하는지 확인할 최소 TCP 에코
서버 예제. plan/serverlib_echo_import_0708.md 설계에 따른 2단계
작업.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: EchoClient 예제 반입 및 솔루션 등록

**Files:**
- Create: `examples/EchoClient/EchoClient.csproj`
- Create: `examples/EchoClient/Program.cs` (원본 무수정 복사)
- Modify: `IDLE_RPG.sln`

**Interfaces:**
- Consumes: Task 1의 `ServerLib.ServerNet.CreateClient()`, `ServerLib.Interface.IClientConnection`, `ServerLib.Core.Serialization.BinaryPacketSerializer`, `ServerLib.Core.Serialization.Packets.EchoPacket`
- Produces: `127.0.0.1:9000`에 접속해 표준입력 라인을 `EchoPacket`으로 송신하고 응답을 콘솔에 출력하는 실행 파일 `examples/EchoClient` — Task 5의 스모크 테스트가 `dotnet run --project examples/EchoClient`로 구동한다. 입력 `"exit"` 수신 시 루프를 빠져나와 정상 종료한다.

- [ ] **Step 1: EchoClient 디렉터리 생성 및 소스 복사**

```bash
cd /e/project/IDLE_RPG
mkdir -p examples/EchoClient
cp "/e/project/ClaudeCodeStudy/EchoClient/Program.cs" examples/EchoClient/Program.cs
cp "/e/project/ClaudeCodeStudy/EchoClient/EchoClient.csproj" examples/EchoClient/EchoClient.csproj
```

- [ ] **Step 2: ProjectReference 경로 조정**

`examples/EchoClient/EchoClient.csproj`의 11번째 줄:

Before:
```xml
    <ProjectReference Include="..\ServerLib\ServerLib.csproj" />
```

After:
```xml
    <ProjectReference Include="..\..\ServerLib\ServerLib.csproj" />
```

- [ ] **Step 3: Program.cs가 원본과 동일한지 확인**

Run:
```bash
diff /e/project/ClaudeCodeStudy/EchoClient/Program.cs /e/project/IDLE_RPG/examples/EchoClient/Program.cs
```
Expected: 출력 없음(완전 동일).

- [ ] **Step 4: 솔루션에 등록 (examples 폴더)**

Run:
```bash
dotnet sln IDLE_RPG.sln add examples/EchoClient/EchoClient.csproj -s examples
```
Expected: `Project `examples\EchoClient\EchoClient.csproj` added to the solution.`

- [ ] **Step 5: 빌드 검증**

Run:
```bash
dotnet build examples/EchoClient/EchoClient.csproj -c Debug
```
Expected: `Build succeeded.` / 오류 0.

- [ ] **Step 6: 커밋**

```bash
git add examples/EchoClient IDLE_RPG.sln
git commit -m "$(cat <<'EOF'
추가: ServerLib 검증용 EchoClient 예제 반입

EchoServer(Task 2)와 짝을 이루는 대화형 TCP 에코 클라이언트 예제.
plan/serverlib_echo_import_0708.md 설계에 따른 3단계 작업.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: EchoExample.Tests 반입, 버전 정합화, 자동 테스트 통과 확인

**Files:**
- Create: `tests/EchoExample.Tests/EchoExample.Tests.csproj`
- Create: `tests/EchoExample.Tests/EchoEndToEndTests.cs` (원본 무수정 복사)
- Create: `tests/EchoExample.Tests/EchoPacketRoundTripTests.cs` (원본 무수정 복사)
- Modify: `IDLE_RPG.sln`

**Interfaces:**
- Consumes: Task 1의 `ServerLib.ServerNet`, `ServerLib.Interface.*`, `ServerLib.Core.Serialization.*`(테스트가 직접 소켓을 열어 EchoServer 핸들러 로직을 재현 — `IServerListener`/`IClientConnection` public API만 사용, `InternalsVisibleTo` 불필요)
- Produces: `dotnet test tests/EchoExample.Tests`로 실행 가능한 xUnit 테스트 스위트(`EchoEndToEndTests`, `EchoPacketRoundTripTests`) — 이후 회귀 검증에 재사용.

- [ ] **Step 1: EchoExample.Tests 디렉터리 생성 및 소스 복사**

```bash
cd /e/project/IDLE_RPG
mkdir -p tests/EchoExample.Tests
cp "/e/project/ClaudeCodeStudy/EchoExample.Tests/EchoEndToEndTests.cs" tests/EchoExample.Tests/EchoEndToEndTests.cs
cp "/e/project/ClaudeCodeStudy/EchoExample.Tests/EchoPacketRoundTripTests.cs" tests/EchoExample.Tests/EchoPacketRoundTripTests.cs
cp "/e/project/ClaudeCodeStudy/EchoExample.Tests/EchoExample.Tests.csproj" tests/EchoExample.Tests/EchoExample.Tests.csproj
```

- [ ] **Step 2: 테스트 소스가 원본과 동일한지 확인**

Run:
```bash
diff /e/project/ClaudeCodeStudy/EchoExample.Tests/EchoEndToEndTests.cs /e/project/IDLE_RPG/tests/EchoExample.Tests/EchoEndToEndTests.cs
diff /e/project/ClaudeCodeStudy/EchoExample.Tests/EchoPacketRoundTripTests.cs /e/project/IDLE_RPG/tests/EchoExample.Tests/EchoPacketRoundTripTests.cs
```
Expected: 두 명령 모두 출력 없음(완전 동일).

- [ ] **Step 3: csproj를 저장소 컨벤션에 맞게 재작성**

`tests/EchoExample.Tests/EchoExample.Tests.csproj` 전체를 다음 내용으로 교체:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
    <RootNamespace>EchoExample.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.4" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <!-- ServerLib를 소스 참조: public API만 사용 (internal 접근 불필요, InternalsVisibleTo 대상이 아님) -->
    <ProjectReference Include="..\..\ServerLib\ServerLib.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: 솔루션에 등록 (기존 tests 폴더)**

Run:
```bash
dotnet sln IDLE_RPG.sln add tests/EchoExample.Tests/EchoExample.Tests.csproj -s tests
```
Expected: `Project `tests\EchoExample.Tests\EchoExample.Tests.csproj` added to the solution.`

- [ ] **Step 5: 테스트 실행 및 통과 확인**

Run:
```bash
dotnet test tests/EchoExample.Tests/EchoExample.Tests.csproj
```
Expected: `Passed!` 요약 라인, 실패(Failed) 0건. (`EchoEndToEndTests` + `EchoPacketRoundTripTests`에 정의된 모든 테스트 케이스가 통과해야 함)

- [ ] **Step 6: 커밋**

```bash
git add tests/EchoExample.Tests IDLE_RPG.sln
git commit -m "$(cat <<'EOF'
테스트: ServerLib 에코 예제 자동 검증 테스트 반입

EchoServer/EchoClient(Task 2·3) 동작을 실소켓으로 검증하는 xUnit
스위트 반입. 패키지 버전을 저장소 기존 테스트 프로젝트 컨벤션에
맞춤(coverlet.collector 추가, xunit.runner.visualstudio 3.1.4).
plan/serverlib_echo_import_0708.md 설계에 따른 4단계 작업.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: 실제 TCP 에코 왕복 스모크 테스트

**Files:**
- (파일 변경 없음 — Task 1~4로 반입된 실행 파일의 런타임 동작 검증)

**Interfaces:**
- Consumes: Task 2의 `examples/EchoServer`, Task 3의 `examples/EchoClient` 실행 파일

- [ ] **Step 1: 서버·클라이언트 사전 빌드 (런타임 지연 방지)**

```bash
cd /e/project/IDLE_RPG
dotnet build examples/EchoServer/EchoServer.csproj -c Debug
dotnet build examples/EchoClient/EchoClient.csproj -c Debug
```
Expected: 두 빌드 모두 `Build succeeded.`

- [ ] **Step 2: EchoServer를 백그라운드로 기동**

```bash
cd /e/project/IDLE_RPG
rm -f /tmp/echo_server.log /tmp/echo_client.log
nohup dotnet run --project examples/EchoServer --no-build -c Debug > /tmp/echo_server.log 2>&1 &
echo $! > /tmp/echo_server.pid
```

- [ ] **Step 3: 서버 기동 완료를 로그로 확인 (최대 10초 대기)**

```bash
for i in $(seq 1 20); do
  grep -q "에코 서버 시작" /tmp/echo_server.log 2>/dev/null && break
  sleep 0.5
done
grep "에코 서버 시작" /tmp/echo_server.log
```
Expected: `에코 서버 시작 — 포트 9000  [종료: 아무 키나 누르세요]` 라인 출력.

- [ ] **Step 4: EchoClient로 메시지 1건 전송 후 정상 종료**

```bash
cd /e/project/IDLE_RPG
printf 'hello-serverlib\nexit\n' | dotnet run --project examples/EchoClient --no-build -c Debug > /tmp/echo_client.log 2>&1
cat /tmp/echo_client.log
```
Expected: `[서버 응답] hello-serverlib` 라인이 출력에 포함됨.

- [ ] **Step 5: 서버 쪽 수신 로그도 확인**

```bash
grep '\[수신\] "hello-serverlib"' /tmp/echo_server.log
```
Expected: 해당 라인 1건 출력(왕복 성공 증거).

- [ ] **Step 6: 백그라운드 서버 프로세스 정리**

```bash
kill $(cat /tmp/echo_server.pid) 2>/dev/null || true
```

- [ ] **Step 7: 실패 시 조치**

Step 4 또는 5가 기대 출력을 내지 않으면:
1. `cat /tmp/echo_server.log`로 서버 콘솔 전체 로그를 확인해 예외/오류 라인 확인
2. `netstat -ano | grep 9000` (Windows: `netstat -ano | findstr 9000`)으로 포트 9000 점유 여부 확인 — 점유 중이면 해당 프로세스 종료 후 Step 2부터 재시도
3. Task 1~4의 `diff` 검증 결과를 다시 확인해 소스 반입 과정에서 누락된 파일이 없는지 재확인

Step 6까지 정상 완료되면 커밋할 파일 변경이 없으므로 이 태스크는 커밋하지 않는다.

---

### Task 6: CLAUDE.md 문서 갱신

**Files:**
- Modify: `CLAUDE.md`

**Interfaces:**
- (해당 없음 — 문서 전용 변경)

- [ ] **Step 1: "현재 플랜 문서 목록" 표에 이번 설계 문서 등록**

`CLAUDE.md`에서 아래 블록을 찾는다:

Before:
```markdown
| [gameserver_domain_scaffold_0704.md](plan/gameserver_domain_scaffold_0704.md) | mermaid classDiagram 기반 GameServer 도메인 모델(스탯·전투·아이템·엔티티·보상) 스켈레톤 스캐폴딩. §8에 2026-07-05 기준 구현 상태 classDiagram·원본 대비 델타표 추가 |
| [battle_system_0705.md](plan/battle_system_0705.md) | 방치형 전투 플로우 설계(온라인 실시간 틱 + 오프라인 수식 하이브리드) 및 TDD 구현: 스탯 집계 파이프라인·버프·보상·오프라인 정산·코드리뷰 수정(F1~F11)·단일 Player vs Monster `BattleLoop` 무한 루프 완료(`GameServer.Tests` 63개), Stage/Wave/Spawner/스킬/부활코스트는 다음 사이클 |
```

After:
```markdown
| [gameserver_domain_scaffold_0704.md](plan/gameserver_domain_scaffold_0704.md) | mermaid classDiagram 기반 GameServer 도메인 모델(스탯·전투·아이템·엔티티·보상) 스켈레톤 스캐폴딩. §8에 2026-07-05 기준 구현 상태 classDiagram·원본 대비 델타표 추가 |
| [battle_system_0705.md](plan/battle_system_0705.md) | 방치형 전투 플로우 설계(온라인 실시간 틱 + 오프라인 수식 하이브리드) 및 TDD 구현: 스탯 집계 파이프라인·버프·보상·오프라인 정산·코드리뷰 수정(F1~F11)·단일 Player vs Monster `BattleLoop` 무한 루프 완료(`GameServer.Tests` 63개), Stage/Wave/Spawner/스킬/부활코스트는 다음 사이클 |
| [serverlib_echo_import_0708.md](plan/serverlib_echo_import_0708.md) | ClaudeCodeStudy `ServerLib`(고성능 소켓 서버 라이브러리) 소스 반입 설계. `ServerLib`는 루트 직속(GameServer와 동급), `EchoServer`/`EchoClient`는 `examples/`, 자동 테스트는 `tests/EchoExample.Tests`. 에코 왕복 스모크 테스트로 1차 검증, GameServer 통합은 다음 사이클 |
```

- [ ] **Step 2: "예제 코드 위치" 섹션에 신규 프로젝트 한 줄 요약 추가**

`CLAUDE.md`에서 아래 블록을 찾는다:

Before:
```markdown
**예제 코드 위치:** 각 프로젝트의 `Program.cs`가 라이브러리/서버 사용 예제 역할을 한다.
프로젝트가 늘어남에 따라 이 섹션에 프로젝트별 한 줄 요약을 추가할 것.

새 기능을 추가할 때 Program.cs의 예제도 함께 업데이트할 것.
```

After:
```markdown
**예제 코드 위치:** 각 프로젝트의 `Program.cs`가 라이브러리/서버 사용 예제 역할을 한다.
프로젝트가 늘어남에 따라 이 섹션에 프로젝트별 한 줄 요약을 추가할 것.

- `ServerLib`: 고성능 .NET 10 비동기 소켓 서버 라이브러리(System.IO.Pipelines 기반 Zero-copy 송수신, 세션·하트비트·RUDP 포함). ClaudeCodeStudy에서 소스 반입, 향후 `GameServer`가 참조할 네트워킹 기반.
- `examples/EchoServer`: `ServerLib.ServerNet.CreateListener()`로 포트 9000 TCP 에코 서버를 띄우는 최소 예제. 실행: `dotnet run --project examples/EchoServer`.
- `examples/EchoClient`: `ServerLib.ServerNet.CreateClient()`로 에코 서버에 접속해 콘솔 입력을 송수신하는 예제. 실행: `dotnet run --project examples/EchoClient`.

새 기능을 추가할 때 Program.cs의 예제도 함께 업데이트할 것.
```

- [ ] **Step 3: 변경 사항 확인**

Run:
```bash
git diff CLAUDE.md
```
Expected: Step 1·2에서 지정한 두 군데(표 1행 추가, 불릿 3개 추가)만 변경으로 표시됨.

- [ ] **Step 4: 커밋**

```bash
git add CLAUDE.md
git commit -m "$(cat <<'EOF'
문서: ServerLib 반입 관련 CLAUDE.md 갱신

신규 반입한 ServerLib/EchoServer/EchoClient를 프로젝트 개요의
예제 코드 위치 섹션과 플랜 문서 목록에 등록해 프로젝트 컨벤션을
최신 상태로 유지.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Post-Plan Verification

전체 태스크 완료 후 최종 확인:

```bash
cd /e/project/IDLE_RPG
dotnet build IDLE_RPG.sln
dotnet test tests/EchoExample.Tests/EchoExample.Tests.csproj
git log --oneline -6
```
Expected: 솔루션 전체 빌드 성공, 테스트 전부 통과, Task 1~4·6의 커밋 5개(+ Task 5는 무커밋)가 로그에 순서대로 보임.
