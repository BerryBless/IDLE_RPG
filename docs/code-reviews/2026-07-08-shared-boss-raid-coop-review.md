# 종합 코드 리뷰 리포트
**생성:** 2026-07-08  |  **대상:** 전투 멀티플레이 2단계(공유 보스 co-op), 커밋 `997349f..4a806d2` (6개 커밋, diff 1489줄)

대상 파일: `GameServer/Systems/RaidEncounter.cs`(수정), `GameServer/Systems/RaidBroadcastPackets.cs`(신규),
`GameServer/Systems/SessionRaidRunner.cs`(신규), `GameServer/Main.cs`(수정), 관련 테스트 4개 파일,
관련 plan 문서 3개.

---

## 종합 건강 점수

| 도메인 | 점수 | Critical | High | Medium | Low |
|--------|------|----------|------|--------|-----|
| 🏗️ 아키텍처 | 80 / 100 | 0 | 0 | 3 | 5 |
| 🔒 보안 | 82 / 100 | 0 | 0 | 1 | 1 |
| ⚡ 성능 | 74 / 100 | 0 | 1 | 2 | 1 |
| 🎨 스타일 | 88 / 100 | — | 0 | 2 | 5 |
| **종합** | **80.4 / 100** | **0** | **1** | **8** | **12** |

가중치: 보안 35% · 아키텍처 25% · 성능 25% · 스타일 15%

---

## Critical & High 발견사항 ← 머지 전 필수 수정

### [성능] HIGH — 무제한 피해 채널 + 액터의 동기 브로드캐스트 대기 → 지속 지연 시 메모리 무한 증가(OOM)
**위치:** `GameServer/Systems/RaidEncounter.cs:342,451,458`(RunAsync/onStep await), `GameServer/Systems/SessionRaidRunner.cs:634,749-768`

**문제:** `_damageChannel`은 `Unbounded` + `SingleWriter=false`라 접속한 모든 세션(N명)이 매 틱(500ms) `TryWrite`로 무조건 성공한다(총 유입 초당 2N건). 소비자인 단일 액터 루프는 매 스텝 `onStep`(`BroadcastStepAsync`)을 동기 `await`하고, 그 안의 `registry.BroadcastAsync`가 N개 세션 전송 완료까지 대기한다. **발동 조건:** 브로드캐스트 1회 지연이 스로틀 창(150ms)을 지속적으로 초과하는 순간, 액터 소비 속도가 유입 속도(2N/초)를 따라잡지 못해 `_damageChannel`이 무한히 쌓인다 — 이는 이미 문서화된 "지연" 한계(plan §2 "알려진 한계")와는 **별개의 실패 모드(메모리 고갈)**이며, 아래 성능 Medium 발견(스로틀 무력화)이 이 축적을 가속시킨다. 보안 리뷰어도 독립적으로 같은 근본 원인을 DoS 벡터(CWE-400)로 지적했다 — 느린/정지된 클라이언트 1명이 `ISession.SendAsync`에서 무한 블록되면(리스너에 `SessionSendTimeout` 미설정) 브로드캐스트 자체가 영원히 반환하지 못해 위 조건이 확정적으로 발생한다.

**수정(우선순위순):**
1. **근본 해결(권장):** 브로드캐스트를 액터 루프에서 분리 — 별도 채널 + 독립 드레인 태스크로 액터 소비를 네트워크 I/O와 완전히 무관하게 만든다(plan §7에 이미 다음 사이클 과제로 기록됨, 이번 리뷰로 우선순위를 올릴 근거가 명확해짐).
2. **즉시 적용 가능한 완화책:** `Main.cs`의 `listener.Start(...)` 배선에 `listener.SessionSendTimeout`을 유한값(1~2초)으로 설정해 죽은/정지 피어의 송신만 끊고 브로드캐스트 전체가 계속 진행되게 한다(ServerLib이 정확히 이 목적으로 제공하는 노브 — 배선에서 누락됨).
3. **스톱갭(트레이드오프 명시 필요):** `_damageChannel`을 `CreateBounded`로 전환해 메모리 상한을 강제할 수 있으나, `DropOldest` 등은 해당 플레이어의 피해 제출을 조용히 누락시켜 기여도(`_contributions`)를 과소 집계하고 MVP 판정을 왜곡한다 — 임시책으로만 채택.

---

## Medium 발견사항 ← 권장 수정

### [성능] MEDIUM — HP 브로드캐스트 스로틀이 await 이전에 타임스탬프를 찍어 느린 브로드캐스트에서 무력화됨
**위치:** `GameServer/Systems/SessionRaidRunner.cs:754-767` (`BroadcastStepAsync`)
**문제:** `_lastHpBroadcastUtc = now`를 `BroadcastAsync` await **이전**에 갱신한다. 브로드캐스트 1회가 150ms를 넘으면 다음 스텝의 `now - _lastHpBroadcastUtc`가 항상 창을 초과해 스로틀이 절대 억제하지 못하고 매 스텝이 실제 브로드캐스트를 강제당한다 — 보호가 가장 필요한(느린) 구간에서 정확히 보호가 사라지며 위 HIGH 발견의 큐 증가를 가속한다. 건강한 경우(브로드캐스트가 빠름)엔 설계 의도대로 2N²→~3N 억제가 유효함은 확인됨.
**수정:** 타임스탬프를 `await` **완료 이후**에 찍도록 순서를 바꾼다. 근본적으로는 위 HIGH 발견의 브로드캐스트 분리를 적용하면 이 퇴화 자체가 사라진다.
**해소됨(2026-07-08, HIGH 수정과 동시 적용):** `RaidBroadcaster.DrainAsync`(구 `BroadcastDrainAsync`)에서 `_lastHpBroadcastUtc` 갱신을 `await` 완료 이후로 이동.

### [성능] MEDIUM — 세션별 매 틱 `Task.Delay(interval, token)` 반복 할당
**위치:** `GameServer/Systems/SessionRaidRunner.cs:690,706` (`SubmitLoopAsync`)
**문제:** `while(true)` 안에서 매 틱 `Task.Delay`를 새로 호출해 Delay 프라미스 + 내부 Timer + `CancellationTokenRegistration`을 매번 할당한다. 세션 N개 × 500ms → 초당 ~2N건 Gen0 할당이 서버 수명 내내 지속되어, 이 코드베이스의 다른 핫 패스(ArrayPool/무할당 지향)와 어긋난다.
**수정:** 세션당 `PeriodicTimer` 1개를 재사용(`await timer.WaitForNextTickAsync(ctx.Cts.Token)`)하도록 교체한다.
**해소됨(2026-07-09):** `SubmitLoopAsync`가 루프 진입 전 `PeriodicTimer` 1개를 생성해 재사용하도록 교체.

### [보안] MEDIUM — 느린 클라이언트가 레이드 액터를 무한 정지 + 무경계 채널 메모리 고갈(DoS)
**위치:** `GameServer/Main.cs:109`, `GameServer/Systems/SessionRaidRunner.cs:749-789`
**CWE:** CWE-400
**문제:** 위 성능 HIGH 발견과 동일 근본 원인을 보안 렌즈로 재확인. 리스너에 `SessionSendTimeout`/`IdleTimeout`을 설정하지 않아 ServerLib 기본값(무한 대기)이 적용된다. `SessionRegistry.BroadcastAsync`는 전 세션 전송 완료까지 await하며, 수신 버퍼를 비우지 않는 피어에 대해 `SendAsync`가 무한 블록된다(ServerLib 1차 소스 `SocketPipelineSession.cs:93-102`로 확인). `IdleTimeout`은 `LastReceivedAt`(수신 경로) 기반이라 읽기를 멈추고 하트비트만 보내는 피어는 스윕되지 않아 정지가 무한임을 확인. 현재 `IPAddress.Loopback` 한정이라 도달성이 로컬로 제한되어 medium 판정하나, **향후 외부 노출 시 원격 미인증 DoS로 즉시 상승**하는 시한폭탄이다.
**수정:** `listener.SessionSendTimeout`을 유한값으로 설정(위 성능 발견의 완화책 2와 동일한 한 줄 수정으로 해결).
**해소됨(2026-07-08, HIGH 수정과 동시 적용):** `Main.cs`에 `listener.SessionSendTimeout = TimeSpan.FromSeconds(2)` 추가.

### [아키텍처] MEDIUM — `SessionRaidRunner`의 SRP 위반(네트워크 계층이 도메인·보상·직렬화까지 전담)
**위치:** `GameServer/Systems/SessionRaidRunner.cs:46-289`
**문제:** 290줄 단일 클래스가 6가지 변경 이유(보스/인코운터 생성·소유, 장비 착용 도메인 로직, 세션 수명 관리, 피해계산/보상/레벨업, 보상 라우팅, 스로틀링/직렬화/브로드캐스트)를 겸한다.
**수정:** 책임 축으로 분리 — 직렬화·스로틀·브로드캐스트를 담당하는 `RaidBroadcaster`, 보상 큐잉·적용을 담당하는 `RaidRewardApplier`, 세션 등록/해제·제출 루프 수명만 담당하는 얇은 `SessionRaidRunner`로 나눈다.
**해소됨(2026-07-09):** 제안대로 `RaidBroadcaster`/`RaidRewardApplier`를 분리하고 `SessionRaidRunner`를 세션 생명주기·제출 루프 오케스트레이션만 남도록 축소(`plan/battle_raid_coop_0708.md` §2 참고). 부수 효과로 `_byInstanceId` 중복 인덱스(Low 발견)도 함께 제거됨.

### [아키텍처] MEDIUM — 도메인/매퍼/네트워크 계층이 물리적 분리 없이 한 폴더·네임스페이스에 공존
**위치:** `RaidEncounter.cs`, `RaidBroadcastPackets.cs`, `SessionRaidRunner.cs`
**문제:** 감사 요청의 핵심 질문(도메인이 ServerLib 네트워킹 타입을 참조하지 않는가)에 대한 결론 — `RaidEncounter.cs`는 완전히 준수(ServerLib 비참조, 검증 완료). 하지만 세 계층이 모두 `GameServer.Systems` 한 폴더에 섞여 있어 "RaidEncounter는 ServerLib를 참조하면 안 된다"는 규칙이 컴파일러가 아닌 주석·규율로만 강제된다.
**수정:** 도메인(`Systems/Domain`)과 네트워크 배선(`Systems/Net`)을 폴더/프로젝트로 분리해 위반이 컴파일 에러로 드러나게 한다.
**보류(2026-07-09, 사용자 확인):** 이번 사이클의 확립된 평평한 `Systems/` 컨벤션을 바꾸는 결정이라 가치 대비 시급성이 낮다고 판단해 미착수. `plan/battle_raid_coop_0708.md` §7에 향후 과제로 남김.

### [아키텍처] MEDIUM — 게임 전용 패킷이 범용 ServerLib에 위치(의존성 방향 역전, 선행 부채 확대)
**위치:** `ServerLib/Core/Serialization/Packets/MobHpPacket.cs`, `MobDeathPacket.cs`
**문제:** "몹 HP", "MVP/TopDamage" 같은 게임 도메인 개념이 재사용 가능한 범용 소켓 라이브러리에 정의돼 있다. 사이클 1의 선행 부채이지만, 이번 사이클의 `RaidBroadcastPackets`가 바로 이 타입에 결합하며 위 "계층 경계" 발견의 유일한 실제 위반 원인이 됐다.
**수정:** `MobHpPacket`/`MobDeathPacket`을 `GameServer.Net.Packets`로 이관하면 `RaidBroadcastPackets`의 외부 결합이 사라져 경계 원칙이 온전히 성립한다.
**보류(2026-07-09, 사용자 확인):** 벤더 라이브러리(`ServerLib`)를 수정하고 사이클 1의 `SessionBattlePackets`와 그 테스트까지 함께 건드려야 하는 사이클 밖 범위라 판단해 미착수. `plan/battle_raid_coop_0708.md` §7에 향후 과제로 남김.

### [스타일] MEDIUM — `OnConnected`/`OnDisconnected`의 XML 문서가 프로젝트 표준(Thread Safety/Blocking) 미달
**위치:** `GameServer/Systems/SessionRaidRunner.cs:140,164`
**문제:** public 진입점 2개가 `<summary>` 한 줄뿐, CLAUDE.md가 요구하는 `<param>`/`<remarks>`(Thread Safety·Blocking)가 없다. `OnConnected`가 fire-and-forget으로 즉시 반환한다는 계약이 시그니처에 드러나지 않는다.
**수정:** 두 메서드에 `<param name="session">`과 Thread Safety/Blocking을 명시한 `<remarks>`를 추가한다.
**해소됨(2026-07-09):** 제안대로 두 메서드에 `<param>`/`<remarks>`(Thread Context/Blocking/Thread Safety) 추가.

### [스타일] MEDIUM — `SessionRaidRunner`의 에러·경계 분기가 테스트되지 않음(happy path E2E만 존재)
**위치:** `tests/GameServer.Tests/Systems/SessionRaidRunnerEndToEndTests.cs`
**문제:** `TryGetContext` 실패, `TryAdd` 중복, 접속 종료 후 보상 드롭("무해한 no-op"이라 문서화됐으나 미검증), HP 브로드캐스트 스로틀, `SubmitLoopAsync` 예외 경로 — 5가지 설계 결정 분기 중 어느 것도 테스트가 없다. 특히 보상 드롭 경로는 동시성 안전성이 걸린 결정이라 회귀 방지가 필요하다.
**수정:** 최소한 보상 드롭 경로와 스로틀 동작에 대한 단위/통합 테스트를 추가한다.
**해소됨(2026-07-09, 부분):** SRP 분리로 `RaidBroadcaster`/`RaidRewardApplier`가 독립 클래스가 되어 스로틀 경계·보상 라우팅/드롭/Unregister 이후 차단을 단위 테스트로 추가(`RaidBroadcasterTests`/`RaidRewardApplierTests`), `SessionRaidRunner`의 `TryGetContext` 실패/`TryAdd` 중복/중복 해제 방어적 분기도 추가(`SessionRaidRunnerEdgeCaseTests`). `SubmitLoopAsync`의 범용 예외 캐치 경로(`RecordPlayerConnectionError`)는 프로덕션 의존성에 인위적 결함을 주입해야만 트리거 가능해 Medium 심각도 대비 과잉 비용으로 판단해 제외.

---

## Low / 정보성 ← 검토 권장

- [아키텍처] `SessionRaidRunner.cs:66-123` — `_boss` 필드가 `RaidEncounter`의 보스 캡슐화를 의도적으로 무력화(공유 가변 상태 이중 소유, 향후 보스 페이즈 도입 시 데이터 레이스 위험).
- [아키텍처] `RaidEncounter.cs:290-360` — 순수 판정 코어에 `_generation`/`_lastMvpName` 등 브로드캐스트 투영 상태가 혼입(현재는 테스트로 방어됨).
- [아키텍처] `SessionRaidRunner.cs:174-181` — `EquipStarterGear`가 `SessionBattleRunner`와 verbatim 중복(DRY 위반, 시작 장비 ID 4001/5001/6001 중복).
- [아키텍처] `SessionRaidRunner.cs:104-124,203` — 구체 `RaidEncounter` 생성 + `BattleManager` 싱글턴 정적 접근(DIP, 테스트/확장성 저해).
- [아키텍처] `RaidEncounter.cs:437-462` — 액터가 `onStep`을 동기 await(설계 경계는 DIP상 올바름, 런타임 결합은 성능 도메인에서 HIGH로 별도 집계).
- [보안] `SessionRaidRunner.cs:639-660` — 세션당 자원(CTS/Task/딕셔너리 엔트리)에 연결 수 상한 없음(CWE-770, 현재 루프백 한정이라 low).
- [성능] `RaidEncounter.cs:461` — 보스 HP 게이지 메트릭이 브로드캐스트 스로틀을 우회해 매 요청마다 기록됨.
- [스타일] `SessionRaidRunner.cs:105` — 생성자 파라미터 7개, 파라미터 객체화 고려.
- [스타일] `RaidEncounter.cs` RunAsync — `Emit`+`onStep` 호출 블록이 damageStep/deadlineStep 두 번 거의 동일하게 반복(지역 함수로 DRY 가능).
- [스타일] `RaidEncounterBroadcastTests.cs`/`RaidEncounterConcurrencyTests.cs` — `MakeBoss` 테스트 헬퍼 중복.
- [스타일] `SessionRaidRunner.cs:280` — `ArrayPool.Rent` 선언 라인에 인라인 주석 누락(remarks로는 설명됨, 위치 규칙과 어긋남).
- [스타일] `RaidStepBroadcast` — 7-필드 위치 기반 record, 동일 타입 인접 필드로 인자 순서 혼동 위험(현재 일부만 named 인자 사용).

---

## 총평 및 판정

핵심 동시성 설계(단일 액터·단일 라이터 채널 전환·보상 단일 소유 원칙)는 세 도메인(아키텍처·보안·성능) 리뷰어 모두에게서 독립적으로 견고하다는 평가를 받았고, 프로젝트의 엄격한 문서화 규칙도 대체로 잘 지켜졌다(스타일 88점). 다만 세 리뷰어가 **서로 다른 렌즈로 정확히 같은 결함**을 지적했다는 점이 이번 리뷰에서 가장 중요한 신호다 — 리스너에 `SessionSendTimeout`이 없어 정지된 클라이언트가 레이드 액터의 `onStep` 브로드캐스트를 무한 블록시키고, 그 동안 무제한 `_damageChannel`이 무한히 쌓인다(성능=HIGH/메모리 고갈, 보안=Medium/CWE-400 DoS, 아키텍처=Low/런타임 결합). 세 관점의 독립적 수렴은 이 문제가 실재하며 우선순위가 높음을 강하게 뒷받침한다. 다행히 즉시 적용 가능한 완화책(`listener.SessionSendTimeout` 한 줄 설정)이 존재하고, 근본 해결(브로드캐스트를 액터 루프에서 분리)은 이미 plan 문서가 다음 사이클 과제로 예고해 둔 상태다. 그 외 아키텍처의 SRP/계층 분리 지적과 스타일의 문서화·테스트 공백은 기능 정확성에 영향을 주지 않는 유지보수성 개선 항목이다.

**판정: REQUEST CHANGES**

(High 발견 1건 존재 — 종합 점수 80.4는 APPROVE 기준선 이상이나, 판정 기준상 High 발견이 있으면 REQUEST CHANGES가 우선 적용됨. 머지 전 최소 조치로 `listener.SessionSendTimeout` 설정만이라도 반영할 것을 권장한다 — 한 줄 수정으로 HIGH 발견과 보안 Medium 발견을 동시에 완화한다.)
