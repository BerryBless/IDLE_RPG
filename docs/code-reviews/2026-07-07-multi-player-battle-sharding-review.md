# 종합 코드 리뷰 리포트
**생성:** 2026-07-07  |  **대상:** 다중 플레이어 배틀 스레드 샤딩 기능 (commit `299b4b1..2b8b08a`, 8개 코드 파일)

---

## 종합 건강 점수

| 도메인 | 점수 | Critical | High | Medium | Low |
|--------|------|----------|------|--------|-----|
| 🏗️ 아키텍처 | 70 / 100 | 0 | 2 | 2 | 1 |
| 🔒 보안 | 90 / 100 | 0 | 0 | 0 | 4 |
| ⚡ 성능 | 72 / 100 | 0 | 1 | 2 | 3 |
| 🎨 스타일 | 82 / 100 | — | 0 | 2 | 6 |
| **종합** | **79.3 / 100** | **0** | **3** | **6** | **14** |

가중치: 보안 35% · 아키텍처 25% · 성능 25% · 스타일 15%
(90×0.35 + 70×0.25 + 72×0.25 + 82×0.15 = 79.3)

---

## Critical & High 발견사항 ← 머지 전 필수 수정

### [아키텍처] [HIGH] — 샤딩 경로가 `BattleLoop.RunAsync`를 우회해 H2에서 제거한 blocking·취소 모델을 되살림
**위치:** `GameServer/Main.cs:96` (RunShard)
**문제:** `BattleLoop`은 이미 `RunAsync`(async 루프 + `Task.Delay` + `CancellationToken` + 내부 `LogTick`)라는 완결된 실행 모델을 제공하는데, 샤딩 경로(`RunShard`)는 이를 전혀 쓰지 않고 `while(true)` + `Thread.Sleep`으로 루프를 재구현하고 로깅도 `BattleEventLogger`로 별도로 만든다. 2026-07-06 H2 리팩토링이 `Thread.Sleep`→`Task.Delay`로 바꾼 이유가 정확히 "다중 전투 확장 시" 스레드 점유를 없애기 위함이었는데, 이번 확장이 그 근거를 무효화하며 `CancellationToken`도 버려져 **정상 종료 수단이 Ctrl+C 강제 종료뿐**이다. 틱 루프·이벤트 포맷 로직이 async/sync 두 벌로 분산되어 향후 이벤트 추가 시 두 곳을 모두 고쳐야 한다.
**수정 제안:** `BattleLoop.RunAsync`가 `CancellationToken`과 주입식 로그 콜백(`Action<string,BattleTickEvent,Player>` 등)을 받도록 일반화해 단일 플레이어·샤드 양쪽이 같은 루프를 재사용하게 한다. 포맷 로직은 `BattleEventLogger` 하나로 통합하고 `BattleLoop.LogTick`은 제거한다.

### [아키텍처] [HIGH] — 샤딩 오케스트레이션 전체가 테스트 불가능한 컴포지션 루트(`Main.cs`)에 상주
**위치:** `GameServer/Main.cs:58-117`
**문제:** 샤드 파티셔닝 산술, 전용 `Thread` 생성·수명 관리, 샤드 루프, 예외 격리 정책이 전부 `Main.cs`의 최상위 문/로컬 함수에 인라인되어 있어 단위 테스트가 불가능하다. 실제로 이번 사이클에서 테스트된 것은 가장 작은 조각(`ShardBattleRunner.TryTick`)뿐이고, 핵심인 파티셔닝 로직과 샤드 루프는 테스트가 전무하다.
**수정 제안:** 샤딩 스케줄러를 1급 서비스(예: `BattleShardScheduler`)로 추출해 `Partition(pairs)`/`Start(shard, tick, logger, token)`을 노출하고, `Main.cs`는 조립만 담당하게 한다. 파티션 산술을 순수 함수로 분리하면 경계값을 단위 테스트할 수 있다.

### [성능] [HIGH] — 핫 틱 루프에서 `Console.WriteLine` 전역 락으로 샤드 스레드 직렬화
**위치:** `GameServer/Main.cs:70,76`
**문제:** 모든 샤드 전용 스레드가 이벤트/예외 발생 시 `Console.WriteLine`으로 출력하는데, `Console.Out`은 프로세스 전역 synchronized `TextWriter`라 내부 락을 잡는다. 샤딩의 목적 자체가 "독립 전투를 병렬 진행"인데, 로그 출력 구간이 하나의 콘솔 락에서 사실상 직렬화되어 스레드 수(설계상 확장 목표: 수십 스레드 × 100명)를 늘려도 병렬성 이점이 상쇄되는 구조적 병목이다.
**수정 제안:** 각 샤드가 `Channel<string>`(lock-free MPSC 큐)에 이벤트 문자열만 넣고, 단일 전용 로그 소비 스레드가 배치로 flush하도록 바꾼다. 또는 처치 로그를 N회당 1회 샘플링해 출력 빈도 자체를 낮춘다.

---

## Medium 발견사항 ← 권장 수정

- **[아키텍처]** `GameServer/Systems/BattleManager.cs:35-48`, `BattleLoop.cs:85,98` — `BattleLoop`이 전역 정적 싱글턴 `BattleManager.Instance`에 하드 결합(DIP 위반). `Random.Shared` 전환 자체가 "정적 싱글턴이 다중 스레드에서 공유된다"는 숨은 결합의 사후 땜질 증거. → `IDamageCalculator` 인터페이스로 추상화해 `PlayerLevelSystem`과 동일하게 생성자 주입.
- **[아키텍처]** `GameServer/Systems/ShardBattleRunner.cs:254-267` — 비관용 Try 패턴(`BattleTickEvent?` + `out Exception?` 이중 신호) + 광범위 `catch(Exception)`으로 오류를 상태 코드로 강등. → `readonly record struct TickOutcome(Event, Error)` 또는 관용 `bool TryTick(..., out event, out error)`로 통합, 치명적 예외는 재던지기.
- **[성능]** `GameServer/Main.cs:38,80` — 샤드당 전용 OS 스레드 + `Thread.Sleep`으로 99% 유휴 스레드가 스택(~1MB/스레드)을 상주 점유, H2가 없앤 스레드 점유 모델로 회귀. → 샤드 수를 코어 수에 고정하거나 `PeriodicTimer` 기반 순회로 전환.
- **[성능]** `GameServer/Main.cs:60,80` — `Thread.Sleep(고정 500ms)`가 작업 시간을 보정하지 않고 `deltaTime`도 하드코딩되어 틱 레이트 드리프트·샤드 간 진행 속도 편차 발생. → `Stopwatch` 기반 보정 + 실제 경과시간을 `deltaTime`으로 전달.
- **[스타일]** `GameServer/Systems/ShardBattleRunner.cs` (`TryTick` catch 블록) — 광범위 `catch(Exception)` + 호출부 무한 재시도로 결정적 실패(`Rewards=null` 등)가 500ms마다 영구 반복 스팸. → 연속 실패 카운터 후 해당 쌍 격리/제거.
- **[스타일]** `GameServer/Main.cs:105` — 예외 로깅이 `exception.Message`만 출력해 스택트레이스 완전 유실, 원인 진단 불가. → 최소 `exception`(전체 `ToString()`) 출력.

---

## Low / 정보성 ← 검토 권장

- **[아키텍처]** `tests/GameServer.Tests/GlobalUsings.cs:1`, `Main.cs:3` — `BigNumber=double` 별칭이 컴포지션 루트와 테스트 프로젝트에 중복 정의(향후 도메인 분리 시 산탄총 수술 예약)
- **[보안]** `GameServer/Systems/RewardComponent.cs:29` (CWE-362) — 샤드 스레드 안전성이 "몬스터 인스턴스 미공유" 불변식에만 의존. `RewardComponent`의 `Random`은 여전히 `new Random()`(비스레드안전)이며, 현재는 몬스터별 독립 인스턴스라 안전하지만 향후 인스턴스 풀링/공유 보스 도입 시 즉시 레이스 재발 가능
- **[보안]** `GameServer/Systems/BattleManager.cs:106` (CWE-338) — 치명타 판정에 비암호학적 PRNG(`Random.Shared`) 사용. 현재는 네트워크 경계 없어 무해하나, 향후 서버 권위적 보상/경제 결과에 영향 시 예측·파밍 위험 재검토 필요
- **[보안]** `GameServer/Systems/ShardBattleRunner.cs:262` (CWE-396) — 광범위 `catch(Exception)`으로 구조적 오류(OOM 등)까지 무기한 삼킬 가능성
- **[보안]** `GameServer/Main.cs:105` (CWE-209) — 예외 메시지 콘솔 직접 노출(현재는 PII 없어 무해, 향후 로깅 인프라 도입 시 경계 필요)
- **[성능]** `GameServer/Systems/PlayerLevelSystem.cs:85` — 처치 틱마다 `_levelTable.All.Max(...)` LINQ 재열거(불변 상수인데도), 샤딩이 호출 빈도를 수백~수천 배 증폭 → 필드 캐시 권장
- **[성능]** `GameServer/Systems/BattleEventLogger.cs:37` — 이벤트마다 보간 문자열 신규 할당(Gen0 압력), 콘솔 락 finding과 함께 해소 시 완화됨
- **[스타일]** `GameServer/Main.cs:82-84,88` — 장비/몬스터 마스터 데이터 ID 매직 넘버(4001/5001/6001/2003)가 이름 없는 리터럴
- **[스타일]** `GameServer/Main.cs:23` — 틱 간격 `500`이 `ThreadCount`/`PlayersPerThread`와 달리 명명 상수화되지 않음
- **[스타일]** `GameServer/Main.cs:96` — 취소 토큰 없는 `while(true)` — 기존 `BattleLoop`의 취소 지원에서 후퇴(위 아키텍처 HIGH #1과 동일 근본 원인)
- **[스타일]** `tests/GameServer.Tests/Systems/BattleManagerTests.cs` — 리플렉션으로 private `_random` 필드를 검증하는 구현-결합 테스트
- **[스타일]** `GameServer/Systems/BattleEventLogger.cs` (`Format`) — `instanceId`와 `player`를 별도 인자로 받아 논리적으로 같은 대상인데 불일치 가능성 존재
- **[스타일]** `GameServer/Systems/ShardBattleRunner.cs` (`TryTick`) — 메서드 단위 `<remarks>`에 Thread Safety/Memory/Blocking 명시 누락(클래스 레벨로 사실상 커버)

---

## 총평 및 판정

4개 도메인 모두 Critical은 없다. 보안(90점)은 순수 도메인 로직 특성상 전통적 취약점이 없고, 리뷰어가 직접 `Tick` 호출 그래프를 추적해 동시성 안전 주장(`Random.Shared` 전환, 불변 마스터 테이블 공유)이 실제로 유효함을 확인했다 — 다만 `RewardComponent`의 스레드 안전성이 "몬스터 인스턴스 미공유"라는 문서화되지 않은 불변식에 의존한다는 잠재 리스크를 짚었다. 스타일(82점)도 CLAUDE.md의 XML 문서화·동시성 인라인 주석 컨벤션을 신규 파일 전반에서 충실히 지켰다.

문제는 아키텍처(70점)와 성능(72점)에 집중된다. 핵심 원인은 하나로 수렴한다: **이번 샤딩 기능이 정확히 그 확장을 위해 2026-07-06에 만들어진 `BattleLoop.RunAsync`(async, 취소 가능, 스레드 미점유)를 재사용하지 않고, `Main.cs`에 별도의 `while(true)+Thread.Sleep` 루프를 새로 만들었다.** 그 결과 (1) 정상 종료 수단 상실, (2) 로깅/틱 로직 이중화, (3) 샤드당 전용 스레드의 스택 상주 비용, (4) 모든 샤드가 `Console.WriteLine` 전역 락에서 경합해 병렬성이 상쇄되는 구조적 병목까지 이어졌다. 반대로 예외 격리(`ShardBattleRunner.TryTick`)와 `Random.Shared` 전환 자체는 설계 의도와 실행 모두 타당하다고 4개 도메인 모두에서 일관되게 확인됐다.

**판정: REQUEST CHANGES**

- **REQUEST CHANGES 근거**: High 3건(아키텍처 2, 성능 1) 발견 + 종합 점수 79.3(60–79 구간). Critical은 없어 BLOCK 대상은 아니다.
- **머지 전 반드시 검토할 항목**: `BattleLoop.RunAsync` 우회로 인한 취소 불능·이중 로직(아키텍처 HIGH #1), `Console.WriteLine` 전역 락에 의한 샤딩 병렬성 상쇄(성능 HIGH #1). 두 항목 모두 "스레드 샤딩으로 병렬 처리량을 늘린다"는 이번 기능의 핵심 목표와 직접 충돌하므로 우선순위가 높다.
- Medium/Low 항목(회로 차단 없는 무한 예외 재시도, 틱 드리프트, 매직 넘버 등)은 이번 사이클 범위 밖으로 미뤄도 되지만, 후속 사이클에서 다뤄야 할 부채로 기록해둔다.
