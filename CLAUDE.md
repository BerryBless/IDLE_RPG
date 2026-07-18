# IDLE_RPG 프로젝트

## 프로젝트 개요

**목표:** 방치형(Idle/Incremental) 키우기 게임 서버 개발 (.NET 10 기반)

**현재 상태:** 초기 스캐폴드 단계. `TestCode`(hello-world 콘솔 프로젝트)만 존재하며,
게임 도메인(캐릭터 성장·전투·경제 시스템 등)과 서버 아키텍처는 아직 설계되지 않았다.
설계가 확정되는 대로 이 섹션과 `plan/` 문서를 갱신할 것.

**예제 코드 위치:** 각 프로젝트의 `Program.cs`가 라이브러리/서버 사용 예제 역할을 한다.
프로젝트가 늘어남에 따라 이 섹션에 프로젝트별 한 줄 요약을 추가할 것.

- `ServerLib`: 고성능 .NET 10 비동기 소켓 서버 라이브러리(System.IO.Pipelines 기반 Zero-copy 송수신, 세션·하트비트·RUDP 포함). ClaudeCodeStudy에서 소스 반입, `GameServer`가 참조하는 네트워킹 기반.
- `examples/EchoServer`: `ServerLib.ServerNet.CreateListener()`로 포트 9000 TCP 에코 서버를 띄우는 최소 예제. 실행: `dotnet run --project examples/EchoServer`.
- `examples/EchoClient`: `ServerLib.ServerNet.CreateClient()`로 에코 서버에 접속해 콘솔 입력을 송수신하는 예제. 실행: `dotnet run --project examples/EchoClient`.
- `GameServer`: 포트 7777에서 실제 TCP 클라이언트를 받는 게임 서버(`Main.cs`). 로그인은 아직 없음 —
  소켓 연결 시 `SessionPlayerBinder`가 임시 `Player`를 생성해 `session.Context`에 부착한 뒤,
  `SessionRaidRunner`가 시작 장비(4001/5001/6001)를 붙여 **접속한 모든 플레이어가 공유하는 레이드
  보스(몬스터 7001, Hp=5,000,000)**를 함께 공격하게 한다. 보스 HP 변경은 `RaidEncounter` 액터 루프
  하나가 전담하고, 그 결과(`MobHpPacket`/`MobDeathPacket`)를 `ISessionRegistry.BroadcastAsync`로
  접속한 전원에게 동일하게 푸시한다(세션별 독립 몬스터였던 `SessionBattleRunner`는 이번 서버 경로에서
  대체됨 — 클래스·테스트는 git 이력에 보존). 보스는 반격하지 않아(Atk=0) 플레이어는 죽지 않으며,
  제한시간(60초) 내 미처치 시 보상 없이 리셋된다. 해제 시 정리 이벤트를 `logs/game-events.ndjson`에
  남긴다. 포트 7779에 별도 읽기 전용 텔레메트리 리스너(인증 없음, 루프백 한정)도 함께 열어
  `TelemetryPublisher`가 1초 주기로 접속자 수·리스너 통계·공유 보스 HP/세대/MVP를
  `TelemetrySnapshotPacket`으로 브로드캐스트한다(`MonitorServer`가 구독, `plan/web_monitoring_0718.md`).
  실행: `dotnet run --project GameServer`. 이전의 400명 스레드 샤딩 자동배틀 콘솔 데모는
  제거됨(git 이력에 보존, `Systems/BattleLoop.cs` 등 도메인 클래스와 단위 테스트는 그대로 유지).
- `AuthServer`: 포트 7778에서 로그인 요청을 처리하는 별도 인증 서버(`Program.cs`). MongoDB
  (`MongoAccountRepository`, 테스트는 인메모리 페이크)에서 계정을 조회해 PBKDF2 해시
  (`Pbkdf2PasswordHasher`)로 비밀번호를 검증하고, 성공 시 무상태 HMAC-SHA256 토큰
  (`ServerLib.Core.Auth.HmacAuthTokenCodec` — GameServer와 공유해 DB 조회 없이 토큰만으로 검증
  가능)을 `LoginResponsePacket`으로 발급한다. `LoginRequestPacket`(Id=10)만 처리하고 나머지는
  무시(`AuthConnectionHandler`). `dotnet run --project AuthServer -- --seed`로 결정적 더미 계정
  3000개(`user0000`.."user2999", 비밀번호 `Pass!0000` 패턴)를 실제 MongoDB에 시딩할 수 있다
  (`--force` 병기 시 기존 컬렉션을 비우고 재시딩). GameServer가 발급된 토큰을 검증해 실제 로그인
  게이트를 통과시키는 배선은 아직 없음(다음 사이클 과제, `plan/login_mongo_0709.md` §7 참고).
  실행: `dotnet run --project AuthServer`.
- `MonitorServer`: GameServer의 텔레메트리 리스너(포트 7779)를 `ServerLib` 클라이언트로 구독해
  웹 브라우저에 실시간 상태 대시보드를 제공하는 별도 프로세스(`Program.cs`). `TelemetryClientLoop`가
  끊기면 자동 재접속하며 최신 스냅샷을 `TelemetrySnapshotStore`(락 없는 volatile 참조 교체)에 반영하고,
  ASP.NET Core 미니멀 API가 `GET /`(단일 페이지 대시보드)와 `GET /events`(1초 주기 Server-Sent
  Events)를 서빙한다. 웹 의존성(ASP.NET Core)은 이 프로젝트에만 있다 — GameServer/ServerLib는 여전히
  외부 의존성 0(`plan/web_monitoring_0718.md`). 실행: `dotnet run --project MonitorServer` (기본
  포트 8080, `IDLERPG_MONITOR_WEB_PORT`로 재정의 가능). GameServer를 먼저 띄워야 실제 값이 채워진다.

새 기능을 추가할 때 Program.cs의 예제도 함께 업데이트할 것.

## 하네스: Git 자동 커밋 & 푸시 (Git Automator)

**목표:** 보안 검증 → 한국어 커밋 메시지 자동 생성 → 안전한 커밋 & 푸시를 파이프라인으로 자동화한다.

**트리거:** `/commitandpush`, 커밋해줘, 푸시해줘, 변경사항 올려줘, 깃 커밋 요청 시 `commitandpush` 스킬을 사용하라.

**자동 커밋 메시지 전달 (필수 행동 규칙):**
코드·파일 변경을 완료하고 턴을 마치기 직전, WHY 중심 한국어 커밋 메시지를 **`.git/auto_commit_msg.txt`** 에 UTF-8로 작성한다.
- 형식: `{접두사}: {제목}` (접두사: 추가/수정/버그수정/리팩토링/문서/테스트/의존성)
- 제목: 50자 이내, 파일명 나열 금지, WHY 중심
- 본문(선택): `- ` 항목 나열
- 마지막 줄(필수): `Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>`

Stop 훅(`auto-commit.ps1`)이 이 파일을 읽어 커밋하고 즉시 삭제한다. 파일을 남기지 않으면 접두사 기반 폴백 메시지로 커밋된다(안전망).

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-07-03 | 초기 구성 | 전체 | ClaudeCodeStudy 하네스 이식 |
| 2026-07-03 | git 에이전트 frontmatter 정정 | agents/git-security-auditor.md, git-commit-writer.md, git-push-controller.md | 심층 검증에서 발견된 에이전트 타입 미등록 drift 해소 |
| 2026-07-04 | Claude 커밋을 master 대신 claude 브랜치로 자동 라우팅 | scripts/auto-commit.ps1, agents/git-push-controller.md, skills/commitandpush/SKILL.md | master를 Claude 자동 커밋으로부터 보호, 사용자가 명시적으로 병합하기 전까지 깨끗하게 유지 |
| 2026-07-04 | claude 전환 실패 시 master 폴백 커밋 제거, stash 기반 안전 전환으로 교체 | scripts/auto-commit.ps1 | 미커밋 변경이 claude와 충돌해 checkout이 막히자 "무중단 우선" 폴백이 실제로 master에 커밋 2건을 만들어 origin까지 push한 사고 발생 → 보호 브랜치 오염 방지를 무중단보다 우선하도록 정책 변경(전환 실패 시 커밋하지 않고 중단) |

---

## 플랜 문서화 규칙

기능 설계나 아키텍처 결정이 완료되면 `plan/` 디렉토리에 설계 문서를 작성한다.

### 파일 명명 규칙
```
plan/<기능명>_<MMDD>.md
예) plan/character_growth_0710.md
    plan/idle_economy_0715.md
    plan/battle_system_0720.md
```

### 문서 필수 포함 항목
1. **배경 및 목적** — 왜 이 기능이 필요한가, 어떤 문제를 해결하는가
2. **설계 결정** — 채택한 방식과 후보 대안 비교 (표 형식 권장)
3. **컴포넌트 구조** — 디렉토리 트리, 의존 관계 다이어그램
4. **핵심 API** — 주요 사용 패턴 코드 예시
5. **변경 파일 목록** — 신규/수정 파일과 내용 요약
6. **빌드 검증** — 실행 명령어
7. **향후 확장 포인트** — 다음 사이클 추천 항목

### 현재 플랜 문서 목록

| 문서 | 요약 |
|------|------|
| [gameserver_domain_scaffold_0704.md](plan/gameserver_domain_scaffold_0704.md) | mermaid classDiagram 기반 GameServer 도메인 모델(스탯·전투·아이템·엔티티·보상) 스켈레톤 스캐폴딩. §8에 2026-07-05 기준 구현 상태 classDiagram·원본 대비 델타표 추가 |
| [battle_system_0705.md](plan/battle_system_0705.md) | 방치형 전투 플로우 설계(온라인 실시간 틱 + 오프라인 수식 하이브리드) 및 TDD 구현: 스탯 집계 파이프라인·버프·보상·오프라인 정산·코드리뷰 수정(F1~F11)·단일 Player vs Monster `BattleLoop` 무한 루프 완료(`GameServer.Tests` 63개), Stage/Wave/Spawner/스킬/부활코스트는 다음 사이클. §9에 2026-07-08 기준 구현 상태 갱신(드리프트 정정) 추가 |
| [serverlib_echo_import_0708.md](plan/serverlib_echo_import_0708.md) | ClaudeCodeStudy `ServerLib`(고성능 소켓 서버 라이브러리) 소스 반입 설계. `ServerLib`는 루트 직속(GameServer와 동급), `EchoServer`/`EchoClient`는 `examples/`, 자동 테스트는 `tests/EchoExample.Tests`. 에코 왕복 스모크 테스트로 1차 검증, GameServer 통합은 다음 사이클 |
| [client_server_split_0708.md](plan/client_server_split_0708.md) | 클라-서버 분리 1단계: GameServer의 400명 스레드 샤딩 데모를 제거하고 `ServerNet` 기반 실제 TCP 서버(포트 7777)로 교체. 로그인 생략, 소켓 연결마다 `SessionPlayerBinder`가 임시 `Player`를 생성해 `session.Context`에 부착. 실소켓 통합 테스트로 연결→해제 사이클 검증, 게임플레이 프로토콜(OnReceived)과 실제 로그인은 다음 사이클 |
| [battle_multiplayer_0708.md](plan/battle_multiplayer_0708.md) | 전투 멀티플레이 1단계: 접속한 각 세션이 서버 자동 틱(500ms)으로 독립 몬스터를 동시에 사냥, `MobHpPacket`/`MobDeathPacket`을 그 세션에만 푸시(`SessionBattleRunner` 신규, `BattleLoop.RunAsync`에 선택적 `onTick` 콜백 추가). 동시 2연결 세션별 격리 테스트 + 실소켓 스모크로 검증. 공유 보스 co-op·PvP·실로그인은 다음 사이클. 문서 끝에 2026-07-08 기준 구현 상태 갱신(Main.cs 배선이 이후 SessionRaidRunner로 대체됨) 추가 |
| [battle_raid_coop_0708.md](plan/battle_raid_coop_0708.md) | 전투 멀티플레이 2단계: 접속한 모든 플레이어가 공유 레이드 보스(몬스터 7001)를 동시 공격, `RaidEncounter` 액터 루프 하나가 보스 HP를 전담하고 `ISessionRegistry.BroadcastAsync`로 전원에게 `MobHpPacket`/`MobDeathPacket`을 동일하게 푸시(`SessionRaidRunner`/`RaidBroadcastPackets` 신규, `RaidEncounter`에 다중 라이터 지원 + `onStep` 콜백 추가). 세션별 독립 몬스터(`SessionBattleRunner`)는 이 경로에서 대체(git 이력 보존). 다중 라이터 동시성 테스트 + 실소켓 2연결(byte-identical 브로드캐스트 확인) 스모크로 검증. PvP·실로그인·보스 페이즈는 다음 사이클 |
| [login_mongo_0709.md](plan/login_mongo_0709.md) | 로그인 구현: 별도 `AuthServer` 프로세스 + MongoDB(`IAccountRepository` 추상화, 테스트는 인메모리 페이크) + 더미 3000 계정 정확성 검증. 비밀번호는 PBKDF2(`Pbkdf2PasswordHasher`) 해시 저장, 토큰은 무상태 HMAC-SHA256(`ServerLib/Core/Auth/HmacAuthTokenCodec`, GameServer와 공유 예정)으로 발급. 기존에 정의만 돼 있던 `LoginRequestPacket`/`LoginResponsePacket`/`AuthTokenPacket`을 처음 배선(`AuthConnectionHandler`). TDD로 36개 테스트 신규(HMAC 코덱·해셔·3000 정확성·패킷 왕복·실소켓 E2E), 기존 스위트 회귀 없음. GameServer의 토큰 검증 게이트 배선은 다음 사이클 |
| [gameserver_auth_gate_0709.md](plan/gameserver_auth_gate_0709.md) | GameServer 토큰 게이트: AuthServer가 발급한 `AuthTokenPacket`을 `SessionAuthGate`(신규)가 `HmacAuthTokenCodec`으로 검증해 성공 시에만 실제 `Player`(claims.AccountId)를 결합하고 공유 레이드에 참전시키는 강한 관문. 검증 결과는 신규 `AuthTokenAckPacket`(Id=18)으로 명시 통지. `PlayerFactory.CreateTemp`/`SessionPlayerBinder.OnConnected`는 다른 테스트의 픽스처 헬퍼로 계속 쓰이고 있어 삭제 대신 Main.cs 배선만 제거(문서만 갱신). 동시 다중 로그인 시 보상 오배송을 막기 위해 Player instanceId에 accountId+sessionId를 함께 사용 |
| [web_monitoring_0718.md](plan/web_monitoring_0718.md) | 웹 실시간 모니터링 대시보드: GameServer에 읽기 전용 텔레메트리 리스너(포트 7779, 인증 없음) 신설, `RaidEncounter`의 onStep을 `RaidBroadcaster`와 함께 팬아웃 구독하는 `TelemetryPublisher`(용량1 DropOldest 채널로 락 없는 "최신 값만 유지" + 1초 `PeriodicTimer`)가 `TelemetrySnapshotPacket`(Id=19)을 브로드캐스트. 신규 별도 프로세스 `MonitorServer`(ASP.NET Core, `ServerLib`만 참조)가 `TelemetryClientLoop`로 자동 재접속 구독해 `GET /`(대시보드)·`GET /events`(1초 SSE)를 서빙 — 웹 의존성은 이 프로젝트에만 존재, GameServer/ServerLib는 외부 의존성 0 유지. 플레이어별 상세(레벨/골드/기여도)는 크로스 스레드 데이터 레이스 위험으로 v1 스코프에서 명시 제외(다음 사이클). 실소켓 2연결 byte-identical 검증 포함 신규 테스트 6건, 기존 176개 회귀 없음 |

---

## 워크로그 문서화 규칙

완료된 작업 사이클(기능 구현·코드리뷰·버그 수정 등)은 `worklog/` 디렉토리에 한국어 워크로그로
기록한다. `plan/` 문서가 기능의 설계·아키텍처를 다룬다면, 워크로그는 "무슨 작업을 했는지"를
타임라인과 함께 서술하고 실제 구현된 코드를 다이어그램으로 시각화하는 별도 문서다.

파일명은 `worklog/<키워드>_<MMDD>.md` (`plan/` 컨벤션과 동일 스타일).

각 문서는 다음을 포함한다: 개요 · 타임라인(커밋 표) · 변경 사항 요약 · 클래스/시퀀스/순서도
3종 mermaid 다이어그램(**실제 구현된 코드·기능을 그림 — 작업 진행 과정이 아님**) · 검증 결과 ·
관련 문서 링크 · 향후 과제.

**mermaid 필수 규칙:** 줄바꿈은 반드시 `<br/>`를 쓴다(`\n`은 렌더링되지 않는다 —
`plan/battle_system_0705.md`에 기록된 코드리뷰 교훈).

**트리거:** 워크로그 작성, 작업 문서화, 작업 결과 정리, 이번 작업 기록해줘, `/worklog` 요청 시
`worklog` 스킬을 사용하라.

### 현재 워크로그 목록

| 문서 | 요약 |
|------|------|
| [battle_sharding_0707.md](worklog/battle_sharding_0707.md) | 다중 플레이어 배틀 스레드 샤딩(BattleManager `Random.Shared` 전환·`BattleEventLogger`/`ShardBattleRunner` 신규·`Main.cs` 스레드 샤딩 데모 교체) 구현부터 종합 코드리뷰(79.3점, REQUEST CHANGES) 실행, High 2건(취소 복원·콘솔 락 경합 제거) 후속 수정까지 전체 사이클 |
| [battle_raid_coop_0708.md](worklog/battle_raid_coop_0708.md) | 전투 멀티플레이 2단계 공유 보스 co-op: `RaidEncounter` 다중 라이터(SingleWriter=false)+onStep 브로드캐스트 콜백 확장, `RaidBroadcastPackets` 순수 매퍼·`SessionRaidRunner` 네트워크 배선 신규, `Main.cs`를 세션 레지스트리 기반 공유 보스(7001)로 교체. 접속 전원이 한 보스를 동시 공격·기여 비례 보상, 실 루프백 2연결 바이트 동일성 실증(144/144 통과) + plan 3종 드리프트 정정까지 전체 사이클 |
| [battle_raid_coop_review_0709.md](worklog/battle_raid_coop_review_0709.md) | 공유 보스 co-op 종합 코드 리뷰(HIGH 1·Medium 6·Low 12) 후속 수정 사이클: 브로드캐스트를 레이드 액터 루프에서 채널 드레인 태스크로 분리(HIGH), `SessionRaidRunner`를 `RaidBroadcaster`/`RaidRewardApplier`로 SRP 분리(Medium), `PeriodicTimer` 전환·회귀 테스트 9건 추가, 코드-문서 대조 감사로 커밋 3회에 걸쳐 남은 깨진 XML `<see cref>` 3건 발견·정정까지. 테스트 145→154/154, 빌드 0 오류 |

---

## 인터페이스 및 API 문서화(주석) 규칙

모든 인터페이스, public 클래스의 메서드, 대리자(Delegate), RPC 정의 코드를 생성하거나 수정할 때는 반드시 표준 XML 문서 주석(C# `///`)을 매우 상세히 작성해야 한다. 단순 기능 설명을 넘어 **고성능 시스템 프로그래밍 관점의 제약 조건**을 주석에 반드시 포함할 것.

### 주석 필수 포함 항목 (`<remarks>` 활용)

- **Thread Safety:** `Thread-safe` 또는 `Not Thread-safe` 명시. 콜백이면 어느 스레드 컨텍스트(I/O Thread, 호출 스레드 등)에서 실행되는지 명시.
- **Memory Allocation:** 힙 할당 발생 여부(`Zero-allocation guaranteed` 혹은 내부 할당량 명시). `ReadOnlySpan<byte>` / `ReadOnlyMemory<byte>` 버퍼의 **소유권(Ownership)과 생명주기** 명시.
- **Blocking 여부:** 즉시 반환인지, 동기 블로킹인지, 비동기(Non-blocking)인지 명시.

### 이상적인 주석 예시

```csharp
/// <summary>수신된 로우 패킷 버퍼를 역직렬화하여 내부 이벤트 파이프라인으로 라우팅합니다.</summary>
/// <param name="sessionId">패킷을 송신한 클라이언트 세션의 고유 식별자</param>
/// <param name="packetBuffer">수신된 원시 바이트 데이터 세그먼트</param>
/// <returns>패킷 라우팅 및 처리 성공 여부</returns>
/// <exception cref="InvalidPacketException">패킷 헤더가 손상되었거나 프로토콜 구조와 맞지 않을 때</exception>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> 고성능 네트워크 I/O 스레드 풀에서 직접 호출됩니다.
/// 내부에서 동기 블로킹(DB, File I/O)을 수행하면 전체 수신 루프가 정지됩니다.</description></item>
/// <item><description><b>Memory Policy:</b> <paramref name="packetBuffer"/> 소유권은 메서드 실행 동안만 유효합니다.
/// 반환 후에도 참조하려면 복사본을 생성해야 합니다.</description></item>
/// <item><description><b>Concurrency:</b> Thread-safe. 내부적으로 ConcurrentQueue 및 Interlocked로 락 경합을 최소화합니다.</description></item>
/// </list>
/// </remarks>
bool OnPacketReceived(long sessionId, ReadOnlySpan<byte> packetBuffer);
```

### 네트워크·메모리 관련 선언부 인라인 주석 규칙

네트워크 또는 메모리 관련 **함수·변수·필드를 선언할 때**는, 그것을 선택한 이유를 반드시 **해당 타입/API의 내부 동작**을 근거로 인라인 주석(`//`)으로 달아야 한다.

- 대상: `Socket`, `Pipe`, `PipeReader/Writer`, `Channel<T>`, `ArrayPool<T>`, `MemoryPool<T>`, `IMemoryOwner<T>`, `Memory<T>`, `Span<T>`, `NetworkStream`, `SocketAsyncEventArgs`, `ValueTask`, `SemaphoreSlim`, `ConcurrentQueue/Dictionary` 등 네트워크·메모리 관련 모든 타입의 선언
- 주석 내용: "왜 이 타입/API를 골랐는가" → 반드시 **내부 동작 메커니즘**을 이유로 삼을 것 (단순 기능 설명 금지)

**예시:**

```csharp
// Channel<T>: lock-free MPSC 큐로 구현되어 있어 다수 IO 스레드 → 단일 디스패처 경로에서 락 경합 없이 메시지를 전달
private readonly Channel<IPacket> _dispatchChannel = Channel.CreateUnbounded<IPacket>();

// ArrayPool<byte>.Shared: 고정 크기 버킷 풀로 TLS(Thread-Local Storage) 슬롯을 우선 확인하므로
// 동일 스레드에서 반환·재사용 시 힙 할당 없이 O(1) 반환
private readonly byte[] _recvBuffer = ArrayPool<byte>.Shared.Rent(4096);

// SemaphoreSlim: 커널 전환 없이 스핀-대기 후 관리형 대기로 전환하는 경량 세마포어.
// 짧은 임계 구간에서 Mutex보다 컨텍스트 스위치 비용이 낮아 고빈도 송신 제한에 적합
private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
```

---

## 하네스: 종합 코드 리뷰

**목표:** 아키텍처·보안·성능·스타일 4개 에이전트가 병렬로 코드를 감사하고 단일 리포트로 통합한다.

**트리거:** 코드 리뷰, PR 검토, 코드 감사, 종합 리뷰 요청 시 `code-review-orchestrator` 스킬을 사용하라. 단순 질문(개념 설명 등)은 직접 응답 가능.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-07-03 | 초기 구성 | 전체 | ClaudeCodeStudy 하네스 이식 |
| 2026-07-07 | 결과 보고서를 `docs/code-reviews/`에 영구 보존(수동 실행 시) | docs/code-reviews/2026-07-07-multi-player-battle-sharding-review.md | `_workspace/`가 gitignore 대상이라 2026-07-06 실행 결과(`03_consolidated_report.md`)가 커밋되지 않아 유실됨 — 이번 실행 결과를 `docs/code-reviews/YYYY-MM-DD-<주제>-review.md`로 복사·커밋해 재발 방지 (스킬 자체의 표준 Phase 5 갱신은 범위 밖으로 보류) |

---

## 하네스: 동시성 가드 (.NET 10 고성능 서버)

**목표:** Lock-Free 설계 강제·락 정당화 주석 감사·데드락 정적 분석(생성-검증)을 에이전트 팀으로 조율하고 단일 동시성 리포트를 생성한다.

**트리거:** 동시성 검사, 락 감사, 데드락 분석, Lock-Free 검증, async 데드락, 컨텐션 분석 요청 시 `concurrency-guard-orchestrator` 스킬을 사용하라.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-07-03 | 초기 구성 | 전체 | ClaudeCodeStudy 하네스 이식 |

---

## 하네스: GC 가드 (.NET 10 메모리 최적화)

**목표:** 힙 할당 스캐너·풀링 강제자 병렬 감사 → 교차 검증으로 GC 압력 유발 패턴을 제거하고 ValueTask·Span·ArrayPool을 올바르게 적용한다.

**트리거:** GC 억제, 힙 할당 감사, 메모리 최적화, ArrayPool 검사, ValueTask 검증, boxing 탐지, GC 압력 분석 요청 시 `gc-guard-orchestrator` 스킬을 사용하라.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-07-03 | 초기 구성 | 전체 | ClaudeCodeStudy 하네스 이식 |

---

## 하네스: 파이프라인 아키텍처 (.NET 10 고성능 IO)

**목표:** System.IO.Pipelines 기반 Zero-copy IO 루프와 Channel<T> 락-프리 디스패처를 감독자 패턴으로 설계하고 부하 테스트 감사까지 수행한다.

**트리거:** Pipelines 설계, IO 루프 구현, 디스패처 설계, Zero-copy 서버, PipeReader 설계, Channel 디스패처 요청 시 `pipeline-architect-orchestrator` 스킬을 사용하라.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-07-03 | 초기 구성 | 전체 | ClaudeCodeStudy 하네스 이식 |

---

## 하네스: TDD (테스트 주도 개발)

**목표:** 요구사항 입력 시 Red(실패 테스트)→Green(최소 구현)→Refactor(검증·리팩토링) 사이클을 에이전트 팀으로 완주하고, harness-evolve로 명세 대비 최종 코드의 진화 델타를 포착한다.

**트리거:** TDD, 테스트 먼저 작성, Red-Green-Refactor, TDD 사이클, 기능 구현(TDD) 요청 시 `tdd-orchestrator` 스킬을 사용하라. 진화 리포트는 `/harness-evolve`로 수동 실행 가능.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-07-03 | 초기 구성 | 전체 | ClaudeCodeStudy 하네스 이식 |
| 2026-07-03 | TddSession.csproj 템플릿에 EnableDefaultCompileItems=false 추가 | skills/tdd-orchestrator/SKILL.md | 라이브 스모크에서 NETSDK1022(Compile 중복 항목) 빌드 실패 재현·확인 |

---

## 하네스: 워크로그 (작업 결과 문서화)

**목표:** 완료된 작업 사이클을 한국어로 문서화한다 — 개요·타임라인·변경 사항과 함께 실제
구현된 코드/기능을 3종 mermaid(클래스·시퀀스·순서도)로 그려 `worklog/<키워드>_<MMDD>.md`
1개 파일에 담고 "현재 워크로그 목록" 표에 등록한다.

**트리거:** 워크로그 작성, 작업 문서화, 작업 결과 정리, 이번 작업 기록해줘, `/worklog` 요청 시
`worklog` 스킬을 사용하라. 다이어그램은 작업 과정이 아니라 구현된 코드 자체를 그린다.

**변경 이력:**
| 날짜 | 변경 내용 | 대상 | 사유 |
|------|----------|------|------|
| 2026-07-07 | 초기 구성 | 전체 | 작업 결과를 한국어 서술 + 3종 mermaid로 문서화하는 하네스 신설. `docs/`와 대소문자 충돌 위험이 있는 `Docs/` 대신 `worklog/`를 채택(gitignore `_work*/` 패턴 미매칭 확인됨) |
