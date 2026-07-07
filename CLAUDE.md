# IDLE_RPG 프로젝트

## 프로젝트 개요

**목표:** 방치형(Idle/Incremental) 키우기 게임 서버 개발 (.NET 10 기반)

**현재 상태:** 초기 스캐폴드 단계. `TestCode`(hello-world 콘솔 프로젝트)만 존재하며,
게임 도메인(캐릭터 성장·전투·경제 시스템 등)과 서버 아키텍처는 아직 설계되지 않았다.
설계가 확정되는 대로 이 섹션과 `plan/` 문서를 갱신할 것.

**예제 코드 위치:** 각 프로젝트의 `Program.cs`가 라이브러리/서버 사용 예제 역할을 한다.
프로젝트가 늘어남에 따라 이 섹션에 프로젝트별 한 줄 요약을 추가할 것.

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
| [battle_system_0705.md](plan/battle_system_0705.md) | 방치형 전투 플로우 설계(온라인 실시간 틱 + 오프라인 수식 하이브리드) 및 TDD 구현: 스탯 집계 파이프라인·버프·보상·오프라인 정산·코드리뷰 수정(F1~F11)·단일 Player vs Monster `BattleLoop` 무한 루프 완료(`GameServer.Tests` 63개), Stage/Wave/Spawner/스킬/부활코스트는 다음 사이클 |

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
