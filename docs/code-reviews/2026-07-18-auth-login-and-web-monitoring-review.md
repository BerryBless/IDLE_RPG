# 종합 코드 리뷰 리포트
**생성:** 2026-07-18  |  **대상:** `claude` 브랜치 vs `master`(merge-base `f3a908a`) 전체 diff — 3개 미리뷰 커밋 통합
(`a4ec12f` 별도 AuthServer+MongoDB 로그인, `e5e0034` GameServer 인증 게이트 배선, `40476cf` 웹 실시간 모니터링 대시보드/MonitorServer)

---

## 종합 건강 점수

| 도메인 | 점수 | Critical | High | Medium | Low |
|--------|------|----------|------|--------|-----|
| 🏗️ 아키텍처 | 84 / 100 | 0 | 0 | 3 | 4 |
| 🔒 보안 | 58 / 100 | 1 | 0 | 4 | 3 |
| ⚡ 성능 | 80 / 100 | 0 | 1 | 1 | 2 |
| 🎨 스타일 | 82 / 100 | — | 0 | 5 | 7 |
| **종합** | **73.6 / 100** | **1** | **1** | **13** | **16** |

가중치: 보안 35% · 아키텍처 25% · 성능 25% · 스타일 15%

---

## Critical & High 발견사항 ← 머지 전 필수 수정

### [보안] [CRITICAL] — 소스에 하드코딩된 개발용 HMAC 서명 비밀키 폴백 (토큰 위조 → 인증 완전 우회)
**위치:** `AuthServer/Configuration/AuthServerConfig.cs:244`, `GameServer/Main.cs:845`
**CWE:** CWE-798 (Use of Hard-coded Credentials)
**문제:** `IDLERPG_AUTH_HMAC_SECRET` 환경변수가 없으면 AuthServer와 GameServer 양쪽 모두 소스에 리터럴로 박힌 `"dev-only-insecure-hmac-secret-change-me"`를 HMAC-SHA256 토큰 서명/검증 키로 그대로 사용한다. 이 키는 공개(git) 저장소에 평문으로 들어 있으므로, 운영 배포에서 환경변수 설정을 누락하면 공격자가 이 알려진 키로 임의 `accountId`를 담은 유효 토큰을 직접 서명해 만들 수 있다. `HmacAuthTokenCodec`은 무상태(DB 조회 없음)라 서명만 일치하면 통과하므로, 자격증명 없이 어떤 계정으로도 `SessionAuthGate`를 완전히 우회한다. 현재 완화책은 기동 시 콘솔 경고와 루프백 바인딩뿐이며, 둘 다 기본키 사용 자체를 막지는 못한다.
**수정:** 개발 폴백 상수를 제거하고, 비밀키가 없거나 최소 길이(예: 32바이트) 미만이면 프로세스를 기동하지 않고 즉시 예외로 종료(fail-fast)한다. 최소한 Release 빌드에서는 폴백 사용을 금지하고, 두 서버가 반드시 동일한 외부 주입(환경변수/시크릿 매니저) 값을 쓰도록 강제한다.

### [성능] [HIGH] — PBKDF2 동기 CPU 블로킹이 네트워크 IO 스레드를 점유
**위치:** `AuthServer/Login/AuthConnectionHandler.cs:315`
**문제:** `OnReceived`는 네트워크 IO 스레드 풀에서 직접 호출되는데, 그 안에서 `Pbkdf2PasswordHasher.Verify`가 10만 회 반복 PBKDF2를 동기(non-async) CPU 연산(수십 ms)으로 수행한다. 로그인 폭주(재접속 러시·무차별 대입 시도)나 동시 다중 로그인 시, 검증 1건당 IO 스레드 1개가 수십 ms씩 묶여 스레드 풀 워커 수를 넘는 요청이 큐잉되고 전체 수신 처리량이 붕괴할 수 있다(예: 워커 8개, 동시 로그인 100건×50ms → 마지막 요청 ~600ms 지연).
**수정:** `LoginService.AuthenticateAsync`에서 해시 검증을 `Task.Run` 오프로드 또는 CPU 바운드 전용 제한 워커 큐(코어 수 제한 세마포어/채널)로 분리해 IO 스레드가 즉시 반환되도록 한다.

---

## Medium 발견사항 ← 권장 수정

### [보안] [MEDIUM] — 예측 가능한 시딩 자격증명을 실제 MongoDB에 가드 없이 기록
**위치:** `AuthServer/Seeding/AccountSeeder.cs:698`, `AuthServer/Program.cs:530`
**CWE:** CWE-798
**문제:** `--seed`가 결정적 패턴(`user0000..`, `Pass!0000..`) 3000계정을 실제 `IDLERPG_MONGO_CONN` 대상 DB에 그대로 기록하며, 개발/운영 DB 구분 가드가 없다.
**수정:** `--seed` 실행 시 대상이 로컬/개발 DB인지 검증하거나 명시적 개발 플래그를 요구하고, 운영 연결 문자열에는 시딩을 거부한다.

### [보안] [MEDIUM] — 자격증명·인증 토큰 평문 전송(TLS 미도입)
**위치:** `AuthServer/Program.cs:503`, `GameServer/Main.cs:956`
**CWE:** CWE-319
**문제:** `LoginRequestPacket.Password`와 발급된 `AuthTokenPacket`이 암호화 없이 오간다. 현재는 루프백 바인딩으로 완화되지만 `IPAddress.Any` 전환 시 즉시 탈취 위험.
**수정:** TLS 도입 전까지 루프백 전용을 코드로 강제하고, `IPAddress.Any` 전환은 TLS 완료를 전제 조건으로 문서·코드에 못박는다.

### [보안] [MEDIUM] — 로그인 사용자명 열거 타이밍 사이드채널
**위치:** `AuthServer/Login/LoginService.cs:425`
**CWE:** CWE-203
**문제:** 계정 미존재 시 해시 검증을 생략하고 즉시 반환해, 응답 시간 차이로 유효 계정명을 열거할 수 있다.
**수정:** 계정이 없을 때도 더미 해시에 대해 동일한 PBKDF2 Verify를 수행해 성공/실패 경로 시간을 균일화한다.

### [보안] [MEDIUM] — 로그인 무차별 대입 방어(속도 제한·계정 잠금) 부재
**위치:** `AuthServer/Login/AuthConnectionHandler.cs:306`
**CWE:** CWE-307
**문제:** 연결당 로그인 시도 횟수 제한, 계정/IP 단위 속도 제한·잠금이 전혀 없다.
**수정:** 실패 카운터 + 지수 백오프 또는 임시 잠금, 연결당 시도 상한 도입.

### [성능] [MEDIUM] — 유니크 인덱스 보장이 `--seed` 경로에만 묶임
**위치:** `AuthServer/Program.cs:469`
**문제:** `EnsureIndexesAsync`가 시딩 경로에서만 호출돼, 시딩 없이 기동한 배포에서는 로그인마다 컬렉션 풀스캔이 발생할 수 있다.
**수정:** 정상 기동 경로(listener.Start 전)에서도 `EnsureIndexesAsync`를 1회 멱등 호출한다.

### [아키텍처] [MEDIUM] — 텔레메트리 4단계 수동 재매핑 체인(Shotgun Surgery)
**위치:** `GameServer/Systems/TelemetryPublisher.cs`, `ServerLib/.../TelemetrySnapshotPacket.cs`, `MonitorServer/MonitorSnapshot.cs`, `MonitorServer/DashboardHtml.cs`
**문제:** `RaidStepBroadcast → TelemetrySnapshotPacket → MonitorSnapshot → JS`로 4단계 수동 재매핑. 특히 `RaidEventType` enum→byte 의미가 공유 정의 없이 3곳(패킷 주석·MonitorSnapshot 주석·`EVENT_LABELS` 배열)에 중복 기술돼, enum 변경 시 라벨이 조용히 어긋날 수 있다.
**수정:** enum 숫자 계약을 단일 출처로 고정하고, 매핑 지점을 한 곳(예: MonitorServer 전용 매퍼)에 모으거나 enum 개수/라벨 개수 일치를 계약 테스트로 강제한다.

### [아키텍처] [MEDIUM] — 프로세스 간 암묵적 계약이 중복 매직 상수로 흩어짐
**위치:** `GameServer/Main.cs:846`, `AuthServer/Configuration/AuthServerConfig.cs:245`
**문제:** HMAC 기본 시크릿 문자열과 `TelemetryPort`(7779)가 단일 출처 없이 두 프로젝트에 각각 리터럴로 중복돼, 한쪽만 바뀌면 컴파일은 통과하되 런타임에 조용히 깨진다.
**수정:** 공유 상수(HMAC 환경변수 키·기본값, 텔레메트리 포트)를 `ServerLib` 쪽 공통 클래스로 승격해 단일 출처화한다.

### [아키텍처] [MEDIUM] — 도메인 모델에 MongoDB 영속화 속성 침투
**위치:** `AuthServer/Accounts/Account.cs:20`
**문제:** 유스케이스 계층이 소비하는 `Account`에 `[BsonId]`/`[BsonElement]`가 직접 부착돼 특정 영속화 프레임워크에 결합된다.
**수정:** 영속화 무관 도메인 `Account`와 BSON 매핑 문서를 분리하거나, 최소한 `BsonClassMap`을 저장소 내부에서 코드로 등록해 어트리뷰트 의존을 걷어낸다.

### [스타일] [MEDIUM] — HMAC 기본 비밀키 문자열 중복 하드코딩
**위치:** `GameServer/Main.cs:63`, `AuthServer/Configuration/AuthServerConfig.cs:36`
**문제:** 위 아키텍처 발견과 동일 근본 원인(단일 출처 부재)의 스타일 관점 재확인 — 두 값이 정확히 일치해야만 검증이 성립.
**수정:** `ServerLib.Core.Auth`에 공유 상수 클래스로 승격.

### [스타일] [MEDIUM] — 예외를 통째로 삼키는 무로깅 catch
**위치:** `AuthServer/Login/AuthConnectionHandler.cs:61`, `GameServer/Systems/SessionAuthGate.cs:88`
**문제:** `catch { ... }`로 예외를 로그 없이 뭉갠다. `SessionAuthGate`는 `GameEventSink`를 이미 갖고도 실패 경로를 기록하지 않아 재현 어려운 버그가 관측 불가능해진다.
**수정:** `catch (Exception ex)`로 좁혀 최소한 sink에 기록한다.

### [스타일] [MEDIUM] — 인터페이스 밖 public 메서드에 필수 문서 주석 누락(프로젝트 규칙 위반)
**위치:** `AuthServer/Accounts/MongoAccountRepository.cs:44`(`EnsureIndexesAsync`), `:79`(`DropAllAsync`)
**문제:** CLAUDE.md가 요구하는 Thread Safety/Memory Allocation/Blocking 3종 remarks가 두 public 메서드에 없다. 파괴적 연산(`DropAllAsync`)이라 특히 중요.
**수정:** 프로젝트 표준 `<remarks>` 3종을 추가한다.

### [스타일] [MEDIUM] — MonitorServer 프로세스 전체가 자동 테스트 0건
**위치:** `MonitorServer/`(테스트 프로젝트 부재)
**문제:** `TelemetrySnapshotStore`(volatile 동시성), `TelemetryClientLoop`(재접속), SSE 엔드포인트 등 검증 가치가 높은 코드가 자동 테스트 없이 수동 curl 확인으로만 검증됨.
**수정:** `tests/MonitorServer.Tests` 신설, 최소한 store 갱신/`MarkDisconnected` 동작과 패킷→스냅샷 매핑을 단위 검증.

### [스타일] [MEDIUM] — AuthConnectionHandler 예외/무시 경로 테스트 갭
**위치:** `AuthServer/Login/AuthConnectionHandler.cs:47`
**문제:** 손상 패킷 처리, 무관 패킷 무시 분기가 미테스트(SessionAuthGate는 동일 성격 경로를 이미 커버한 것과 대비).
**수정:** `FakeSession` 기반 단위 테스트 추가(손상 패킷→Success=false, 무관 PacketId→무응답).

---

## Low / 정보성 ← 검토 권장

- [보안] `GameServer/Main.cs:958`, `MonitorServer/Program.cs:1776` — 무인증 텔레메트리(7779)/대시보드 SSE(8080) 노출: 현재 루프백 한정으로 위험 낮음, 외부 노출 전환 시 인증 선행 필요(CWE-306)
- [보안] `AuthServer/Configuration/AuthServerConfig.cs:225` — 인증 없는 MongoDB 연결 문자열 기본값(CWE-1188)
- [보안] `ServerLib/Core/Auth/HmacAuthTokenCodec.cs:2048` — 무상태 토큰 취소(revocation) 수단 부재(CWE-613)
- [아키텍처] `ServerLib/.../TelemetrySnapshotPacket.cs` — 재사용 전송 라이브러리에 게임 도메인 패킷 배치(기존 관례, 의도적 트레이드오프 — 즉시 수정 대상 아님)
- [아키텍처] `GameServer/Main.cs:845`, `MonitorServer/Program.cs:1716` — 프로세스별 설정 취득 전략 불일치(Config 클래스 vs 인라인 env)
- [아키텍처] `AuthConnectionHandler.cs`, `SessionAuthGate.cs`, `TelemetryClientLoop.cs` — 수신 패킷 분기 손수 구현 중복 + 헤더 파싱 방식 불일치(`PacketPool.TryParseHeader` vs `BinaryPrimitives`)
- [아키텍처] `PlayerFactory.cs`, `SessionPlayerBinder.cs` — 운영 미사용 API가 테스트 픽스처 용도로만 프로덕션에 잔존
- [성능] `AuthServer/Seeding/AccountSeeder.cs:717` — 3000개 시딩 해시 순차 계산(90~180초), `Parallel.For`로 단축 가능
- [성능] `MonitorServer/Program.cs:1764` — SSE 핸들러가 매초 클라이언트별 동일 스냅샷 반복 직렬화(1Hz·소수 뷰어라 현재 영향 미미)
- [스타일] `GameServer/Systems/SessionRaidRunner.cs:100` — 생성자 파라미터 8개(직전 리뷰에서 7개도 이미 지적됨)
- [스타일] `GameServer/Systems/TelemetryPublisher.cs:94` — `OnStep` vs `OnStepAsync` 네이밍 불일치
- [스타일] `ServerLib/.../TelemetrySnapshotPacket.cs:95` — `GetBodySize()`의 매직 넘버 44 수기 계산
- [스타일] `MonitorServer/TelemetrySnapshotStore.cs:26` — `volatile` 필드 선언부 인라인 근거 주석 누락(클래스 remarks엔 있음)
- [스타일] `tests/.../SessionAuthGateEndToEndTests.cs:26` — `GetFreePort`/`AllocateBuffer` 테스트 헬퍼 3회 이상 중복
- [스타일] `MonitorServer/DashboardHtml.cs:116` — 레이드 이벤트 코드→라벨 매핑 3곳 중복
- [스타일] `MonitorServer/Program.cs:21` — `TelemetryPort` 프로세스 간 중복 + SSE 주기 매직 리터럴

---

## 총평 및 판정

이번 diff는 세 기능(AuthServer+MongoDB 로그인, GameServer 인증 게이트, 웹 모니터링 MonitorServer)을 아우르며, 아키텍처(레이어 경계·DIP)와 스타일(XML 문서·인라인 근거 주석 준수도)은 전반적으로 양호합니다(84점·82점). 성능도 신규 텔레메트리/SSE 경로에 ArrayPool·PeriodicTimer·용량1 채널 등 GC/락 억제 패턴이 잘 적용돼 있습니다(80점). 그러나 보안 도메인에서 **Critical 1건 — 소스에 하드코딩된 개발용 HMAC 기본 비밀키가 운영에서 환경변수 누락 시 인증을 완전히 우회시킬 수 있는 결함**이 발견됐고, 성능에서도 로그인 경로의 PBKDF2 동기 블로킹이 High로 확인됐습니다. 두 항목 모두 이미 코드에 완화 근거(경고 로그·루프백 바인딩)가 있지만 근본적 방어(fail-fast·오프로드)가 아니라 우연에 기대는 상태입니다.

**판정: BLOCK**

- Critical 1건(하드코딩 HMAC 기본키 → 인증 우회) 발견으로 점수(73.6)와 무관하게 BLOCK 기준 충족
- 머지 전 최소 조치: (1) HMAC 기본키 폴백 제거 또는 fail-fast화, (2) PBKDF2 검증 IO 스레드 오프로드
- 그 외 Medium 13건은 권장 수정(특히 보안 4건은 외부 노출 전 반드시 해소), Low 16건은 후속 개선 백로그로 관리
