# 종합 코드 리뷰 리포트 — 부하·스트레스 하네스 + 서버 하드닝

**생성:** 2026-07-22  |  **대상:** git diff `575511a..e287976` (미리뷰 3커밋, 104파일 / +9,029줄)
**하네스:** 종합 4축(아키텍처·보안·성능·스타일) + 동시성 가드(Lock-Free·락 정당화·데드락 생성-검증)

대상 커밋:
- `c1f54fa` 로컬 실행 편의 스크립트 및 콘솔 상태 로그 신설
- `a95b325` 핑·72시간 안정성 측정용 부하 테스트 툴(`tools/LoadTester`) 신설
- `e287976` 부하·스트레스 테스트 하네스 및 서버 하드닝(세션 프레임 레이트 리밋·유휴 스윕·연결 상한)

---

## 종합 건강 점수

### 4축 리뷰
| 도메인 | 점수 | Critical | High | Medium | Low |
|--------|------|----------|------|--------|-----|
| 🏗️ 아키텍처 | 80 / 100 | 0 | 0 | 5 | 6 |
| 🔒 보안 | 82 / 100 | 0 | 0 | 1 | 4 |
| ⚡ 성능 | 87 / 100 | 0 | 0 | 1 | 8 |
| 🎨 스타일 | 88 / 100 | — | 0 | 1 | 7 |
| **4축 종합** | **≈84 / 100** | **0** | **0** | **8** | **25** |

가중치: 보안 35% · 아키텍처 25% · 성능 25% · 스타일 15%

### 동시성 가드
| 도메인 | 점수 | Critical | High | Medium | Low |
|--------|------|----------|------|--------|-----|
| 🔓 Lock-Free | 95 / 100 | 0 | 0 | 1 | 6 |
| 📝 락 정당화 | 95 / 100 | — | 0 | 0 | 2 |
| ⚡ 데드락 위험 | 61 / 100 | 0 | 1 | 2 | 7 |
| **동시성 종합** | **≈83 / 100** | **0** | **1** | **3** | **15** |

가중치: Lock-Free 35% · 락 정당화 30% · 데드락 35%

**총평 판정: REQUEST CHANGES** — 데드락 도메인의 High 1건(`_sendGate.Dispose()` 고아 대기자, 전역 브로드캐스트 정지 파급 확인)이 유일한 머지 차단급 결함이다. 나머지는 Critical/High 없이 Medium 이하이며, 하드닝 3종 토글의 opt-in 기본 동작 보존·ServerLib 경계·XML 문서/인라인 주석 규칙·`.bat` ASCII 규칙은 전부 준수가 확인됐다.

---

## HIGH — 머지 전 필수 수정

### [동시성/데드락] HIGH — `_sendGate.Dispose()`가 게이트 대기자를 고아로 남겨 브로드캐스트 영구 정지
**위치:** `ServerLib/Core/Transport/SocketPipelineSession.cs:420`, `SocketPipelineClient.cs`(동일 패턴)
**문제:** `DisposeAsync`가 소켓·`_sendGate`를 파괴하는데, 유휴 스윕/`Stop()`이 **다른 스레드에서** DisposeAsync를 호출하는 동안 수신 루프의 자동 PONG(`SendPongAsync`→`SendAsync`, 토큰 `CancellationToken.None`) 또는 앱 브로드캐스트가 `_sendGate.WaitAsync`에서 대기 중이면, `SemaphoreSlim.Dispose()`가 대기자를 깨우지 않아 해당 태스크가 **영구 미완료**로 남는다. `SessionRegistry.BroadcastAsync`(호출자 토큰 전달)+`RaidBroadcaster`(서버 수명 토큰)를 거쳐 브로드캐스트 드레인이 프로세스 종료까지 정지되는 전역 파급이 검증자에 의해 확인됐다. PONG 경로가 고아가 되면 수신 루프가 finally에 도달하지 못해 `DecrementIp` 연결 카운트 누수까지 연쇄한다.
**수정:** `_sendGate.Dispose()` 호출을 제거한다. 이 세마포어는 `AvailableWaitHandle`을 사용하지 않으므로 해제할 비관리 자원이 없다(제거 안전). 제거 시 in-flight 보유자의 `finally { _sendGate.Release() }`가 항상 성공해 대기자를 깨우고, 대기자는 파괴된 소켓에서 즉시 실패(fail-fast)로 정상 종료 경로를 탄다. `_sendTimeoutCts?.Dispose()`도 동일 경합 창을 가지므로 함께 재검토한다.
**조치 상태:** ✅ 수정 완료

---

## Medium — 권장 수정

### [동시성/데드락] MEDIUM — `SocketPipelineClient` ConfigureAwait(false) 누락 (계약-구현 불일치)
**위치:** `ServerLib/Core/Transport/SocketPipelineClient.cs` 다수(ConnectAsync/ReceiveAsync/OnReceived await 경로)
**문제:** 소켓·핸들러 await에 `ConfigureAwait(false)`가 누락됐는데, 클래스 주석은 `useSynchronizationContext:false`로 데드락이 해결된다고 과대 주장한다. 그 방어는 Pipe await만 커버하며 소켓/핸들러 await는 미커버 — SynchronizationContext가 있는 호스트에서 고전적 sync-over-async 교착이 성립 가능. 라이브러리 public 경로이므로 "현 소비자가 콘솔뿐"으로 강등 불가.
**수정:** 누락된 소켓/핸들러 await에 `.ConfigureAwait(false)` 추가 + 클래스 주석을 실제 커버 범위로 정정.
**조치 상태:** ✅ 수정 완료

### [동시성/데드락] MEDIUM — 유휴 스윕 콜백을 토큰·시한 없이 await (하드닝 방어 무증상 상실)
**위치:** `ServerLib/Core/Transport/SocketPipelineListener.cs`(IdleSweep 루프, `(session, _)` 토큰 폐기)
**문제:** 유휴 스윕이 `OnIdleTimeout` 콜백을 per-item 토큰을 `_`로 버리고 시한 없이 await한다. 콜백이 행(hang)하면 리스너당 1개뿐인 하드닝 방어 스윕 루프가 영구 정지 → slowloris 방어가 무증상으로 상실된다.
**수정:** 콜백 await에 취소 토큰 전달 또는 시한 바운드(`WaitAsync(timeout)`)를 적용해 콜백 행이 스윕 루프를 멈추지 못하게 한다.
**조치 상태:** ✅ 수정 완료

### [Lock-Free] MEDIUM — `SendTimeout`(`TimeSpan?`) 무동기 프로퍼티의 torn-read
**위치:** `ServerLib/Core/Transport/SocketPipelineSession.cs:105`, `SocketPipelineClient.cs:50`
**문제:** XML 주석은 "Thread-safe, StartReceiving 전후 언제든 설정 가능"을 계약하지만, `TimeSpan?`는 참조가 아니라 멀티워드 구조체(16B)라 CLR이 복사 원자성을 보장하지 않는다. 앱 스레드가 런타임에 재설정하는 동안 I/O 스레드가 읽으면 torn read(HasValue=true인데 ticks 절반만 갱신) 가능. 현 배선(Main.cs는 Start 전 설정)에선 실해 없으나 공개 계약과 구현이 불일치.
**수정:** `long` ticks + 센티널(-1=비활성) 백킹 필드로 전환하고 `Volatile.Read/Write`로 원자적 read/write 보장(계약을 코드로 참되게 만듦). 대안: 계약을 "Start 전 설정 전용, Not thread-safe"로 좁힘.
**조치 상태:** ✅ 수정 완료

### [아키텍처/스타일/보안] MEDIUM — HMAC 시크릿 해석 로직 4중 복제 (Shotgun Surgery / DRY)
**위치:** `AuthServer/Configuration/AuthServerConfig.cs:66`, `GameServer/Main.cs:145`, `tools/LoadTester/Program.cs:182`, `tools/LoadTester/Stress/StressRunner.cs:190`
**문제:** `ResolveHmacSecret()`과 개발용 폴백 리터럴이 4곳에 복제(기존 2곳→4곳으로 악화). 3개 도메인이 동시 지적한 최고신호 항목. 시크릿 정책 변경(최소 길이·회전·폴백 제거) 시 4파일 동시 수정을 요구하고 하나라도 누락되면 발급/검증 불일치로 조용히 인증이 깨진다. 부수적으로 LoadTester는 GameServer의 32바이트 최소 길이 검증을 미러링하지 않는 정책 드리프트(보안 Low)도 동반.
**수정:** 4곳 모두 이미 `ServerLib.Core.Auth`를 참조하므로 거기에 공용 정적 리졸버(예: `HmacSecretResolver.Resolve(allowDebugFallback)`)를 두고 DEBUG 폴백 상수·32바이트 검증을 단일 정의로 통합한다. 프로세스별 경고 문구만 호출부에 남긴다.
**조치 상태:** ✅ 수정 완료

### [아키텍처] MEDIUM — `ConsoleStatusReporter`가 멀티포트 미대응 — 관측 컴포넌트 간 드리프트
**위치:** `GameServer/Main.cs:205,353`
**문제:** `TelemetryPublisher`는 전 포트 통계를 합산하도록 개조됐지만, 같은 커밋의 `ConsoleStatusReporter`는 `gameListeners[0]` 하나만 주입받아 `IDLERPG_GAME_PORT_COUNT>1` 용량 모드에서 콘솔의 `players=`/`rejected`가 1번 포트만 표시 → 텔레메트리 수치와 모순.
**수정:** `ConsoleStatusReporter`도 `IReadOnlyList<IServerListener>`를 받아 합산하거나 합산 로직을 공용 헬퍼로 추출해 두 관측 경로가 공유.
**조치 상태:** ✅ 수정 완료

### [아키텍처] MEDIUM — `IServerListener` 설정 프로퍼티 누적 비대화 (ISP/OCP)
**위치:** `ServerLib/Interface/IServerListener.cs:226`
**문제:** `SessionMaxFramesPerSecond` 추가로 "Start 전에만 설정" 시간 결합 프로퍼티가 7개가 됐다. 하드닝 하나 추가마다 인터페이스·구현·모든 테스트 페이크가 강제 수정(이번 diff에서 `FakeServerListener`가 미사용 멤버 구현 강제 — ISP 위반 실증).
**수정:** 세션/리스너 정책을 불변 옵션 객체(`ListenerOptions`)로 묶어 1회 주입. **주의:** public ServerLib 인터페이스·전 구현·전 테스트 페이크에 파급하는 대형 리팩토링 → 별도 사이클 권고(아래 §다음 사이클 참고).
**조치 상태:** ⏸ 사용자 확인 대기 — 대형 API 재설계(회귀 위험), 별도 사이클 권고

### [아키텍처] MEDIUM — GameServer 컴포지션 루트의 구성 파싱·검증 책임 비대화
**위치:** `GameServer/Main.cs:47-108`
**문제:** env 8종 인라인 파싱·포트 충돌 검증·capacityMode 3중 분기가 400줄에 육박. AuthServer는 `AuthServerConfig`로 분리돼 있어 비대칭이고 구성 규칙이 테스트 불가능한 톱레벨 문에 갇힘.
**수정:** `AuthServerConfig`와 대칭인 `GameServerConfig`(파싱·검증·포트 충돌 가드)를 추출해 단위 테스트를 붙인다.
**조치 상태:** ⏸ 사용자 확인 대기 — 배선 재구성, 별도 사이클 권고

### [아키텍처] MEDIUM — `LoadTestOptions` 갓 레코드 (3개 하네스 관심사 혼재)
**위치:** `tools/LoadTester/Options/LoadTestOptions.cs`
**문제:** 548줄 단일 레코드에 부하·용량·스트레스 3개 관심사 40+속성과 거대 `TryParse` switch가 집중. 측정 전용 툴이라 기준 완화 대상이나 유지보수 리스크.
**수정:** 관심사별 서브 레코드로 분할.
**조치 상태:** ⏸ 사용자 확인 대기 — 측정 툴 리팩토링, 별도 사이클 권고

### [성능] MEDIUM — RTT 히스토그램이 구간마다 stale RTT 재기록 (측정 방법론 갭)
**위치:** `tools/LoadTester/Metrics/MetricsSampler.cs`(BuildInterval)
**문제:** 리포트 주기마다 stale한 `client.Rtt`를 히스토그램에 무조건 재기록해 p50/p95/p99가 패킷 가중이 아닌 "연결·시간 가중" 백분위가 된다. 소크 테스트 서버 건강 판정엔 오히려 타당할 수 있으나(명백한 버그 아님), PASS/FAIL 임계치를 패킷 가중으로 이해하면 판정이 어긋난다.
**수정:** 최소 조치로 "연결·시간 가중 백분위"임을 리포트/문서에 명시해 임계치 해석을 맞춘다(동작 변경 아님). 패킷 가중이 목표면 PONG 수신 이벤트 시점 기록으로 전환.
**조치 상태:** ✅ 문서화 완료(코드 주석 — 연결·시간 가중 정의 명시, 동작 변경 없음)

### [보안] MEDIUM — DoS 하드닝 토글이 기본 비활성(off-by-default) (CWE-770)
**위치:** `GameServer/Main.cs:73-84,338-346`, `docker-compose.yml`
**문제:** IdleTimeout·MaxConnections·MaxFramesPerSecond 3종이 미설정 시 비활성이며 docker-compose도 미설정. 같은 changeset의 스트레스 하네스가 실증한 slowloris·오버사이즈 헤더 증폭·프레임 플러드가 기본 배포에서 열려 있다. "기존 동작 보존"이라 회귀는 아니나 데모된 DoS 경로가 기본값에 노출.
**수정:** 하드닝은 **의도적 opt-in 설계**(기본 동작 보존)이므로 코드 기본값은 바꾸지 않는다. 대신 `docker-compose.yml`/`.env.example`에 안전한 권장값을 명시 배선하고 배포 문서에 "운영 시 필수 설정"으로 명기한다.
**조치 상태:** ✅ docker-compose.yml·.env.example에 권장 하드닝 배선점+가이드 추가(코드 기본값은 opt-in 보존)

---

## Low / 정보성 — 검토 권장 (이번 사이클 수정 안 함, 문서화만)

**동시성/데드락 (Low 7):**
- `SocketPipelineListener.Stop()` sync-over-async — 현 불변식 하 데드락 무위험(확정), 승격 조건 3가지 명시됨
- `SendTimeout` 기본 null로 정지 피어 게이트 점유 — XML remarks에 실패 모드 문서화 + Main.cs 2초 배선으로 완화(외부 소비자 기본값 함정만 잔여)
- StressRunner `DriveAsync` 상호 대기 — 현 시나리오 4종 전부 즉시 반환으로 교착 없음, `IStressScenario` 문서가 교착 구현 지시하는 잠재 함정 → 문서 정정 권고
- AcceptLoop 인라인 `OnClientConnected` await / `GameEventSink.DisposeAsync` 무계 드레인 / `RaidEncounter._damageChannel` Writer 미완료(토큰 None) / StressRunner `ReleaseAsync` await 무시 — 전부 현 구현 유계, 미래 확장점 방어로만 유효

**Lock-Free (Low): `GameEventSink` 필드 간 tearing(표시용 근사, 주석 계약화), 유휴 스윕 over-admission 창(무해, 현 구조가 옳음), 정당한 SemaphoreSlim 4건.
**락 정당화 (Low 2):** 송신 게이트 2건(Session:30, Client:28)에 `[LOCK-REQUIRED]` 표준 블록 형식 부재(내용상 정당화는 충분) — 표준 주석 템플릿 제공됨.
**아키텍처 (Low 6):** 프레임 레이트 축출이 `OnReceiveError` 재사용(정책 위반↔연결 오류 신호 혼재), VirtualClient `Churn` 제어 결합, TelemetryPublisher 생성자 검증 순서/null 요소 미검증, 텔레메트리 루프 수명 소유권이 SessionRaidRunner에, **plan 문서 자기모순**(`plan/stress_harness_0721.md:69` §6 제목 'ServerLib 무변경' vs :79 'ServerLib 변경'), GameEventSink 이중 카운터 다지점 수정.
**보안 (Low 4):** 커밋된 폴백 시크릿(.bat, 루프백 한정), 무인증 텔레메트리 7779 bind 재사용(pre-existing), LoadTester 32바이트 정책 드리프트, 악성 패킷 생성기 대상 allow-list 부재.
**성능 (Low 8):** 유휴 스윕 O(N) 전수 스캔(소용량 timeout×대용량 세션 주의), ControlProbe throwaway 히스토그램 할당, 리소스 샘플링 동기 블로킹, NDJSON per-record flush, churn 재접속 할당, ConnectPacer 타이머 양자화 등 — 전부 저영향.
**스타일 (Low 7):** `StressVerdictEvaluator._totalSeconds` 죽은 필드, `TryParse` 280줄, `FormatLine` 파라미터 8개, `catch(Exception){}` 로깅 없는 삼킴, canonical 상수 로컬 재정의(AuthTokenPacketId=12 등), IdleTimeout/MaxConnections 배선 테스트 공백, ServerLib bare catch.

---

## 총평 및 판정

부하·스트레스 하네스 신설과 서버 하드닝은 전반적으로 견고하다. 하드닝 3종 토글은 opt-in으로 기본 동작을 완전 보존하고(설계 의도 준수), ServerLib 프레임 레이트 리밋은 미설정 시 제로 코스트(성능 회귀 없음), 동시성 설계는 전통적 락 0건의 일관된 lock-free이며, XML 문서/인라인 주석/`.bat` ASCII 등 프로젝트 필수 규칙 준수도 확인됐다. 다만 데드락 도메인의 **High 1건**(`_sendGate.Dispose()` 고아 대기자 → 브로드캐스트 전역 정지)은 좁은 경합이지만 파급이 프로세스 전역이라 머지 전 수정이 필수다. 이와 함께 correctness 계열 Medium(Client ConfigureAwait·유휴 스윕 무토큰 대기·SendTimeout torn-read)과 최고신호 HMAC 시크릿 4중 복제·멀티포트 관측 드리프트를 이번 사이클에 수정하고, 대형 설계 리팩토링(ISP 옵션 객체·GameServerConfig·LoadTestOptions 분할)과 config/문서 항목은 별도로 처리한다.

**판정: REQUEST CHANGES** (High 1건 — 수정 후 재검증 시 APPROVE 상당)
