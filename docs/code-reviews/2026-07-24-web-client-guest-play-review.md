# 종합 코드 리뷰 리포트 — 웹 게스트 플레이(WebClient)

**생성:** 2026-07-24  |  **대상:** WebClient 신설 (커밋 `d101720` + `bf5d40e`, +1,880줄)

리뷰 방식: `code-review-orchestrator` 하네스 — 아키텍처·보안·성능·스타일 4개 에이전트 병렬 감사 → 통합.

---

## 종합 건강 점수

| 도메인 | 점수 | Critical | High | Medium | Low |
|--------|------|----------|------|--------|-----|
| 🏗️ 아키텍처 | 85 / 100 | 0 | 0 | 3 (+3 문서화) | 3 |
| 🔒 보안 | 66 / 100 | 0 | 1 | 2 | 3 (1 문서화) |
| ⚡ 성능 | 80 / 100 | 0 | 1 | 1 (문서화) | 6 (1 문서화) |
| 🎨 스타일 | 82 / 100 | — | 0 | 4 | 8 |
| **종합** | **77 / 100** | **0** | **2*** | **9** | **20** |

가중치: 보안 35% · 아키텍처 25% · 성능 25% · 스타일 15%
`* 보안 High 1건과 성능 High 1건은 동일 근본 원인(WebSocketBrowserChannel 무제한 버퍼)을 두 관점에서 보고한 것 — 실질 High는 1개 결함이다.`

**교차 도메인 수렴 신호:** `WebClient/WebSocketBrowserChannel.cs`의 조각 프레임 조립 로직을 **3개 도메인이 독립적으로 지목**했다 — 보안(메모리 증폭 DoS, High), 성능(무제한 MemoryStream, High), 스타일(어댑터 전면 미테스트, Medium). 한 파일에 세 관점이 겹친 것은 이번 리뷰의 최우선 수정 지점이라는 강한 신호다.

---

## Critical & High 발견사항 ← 머지 전 필수 수정

### [보안 + 성능] HIGH — WebSocket 조각 프레임 조립에 총 메시지 크기 상한 없음 (메모리 증폭 DoS)
**위치:** `WebClient/WebSocketBrowserChannel.cs:38-71` (`ReceiveTextAsync`, `assembled` MemoryStream)
**CWE:** CWE-770 (Allocation of Resources Without Limits or Throttling), CWE-400

**문제:** `EndOfMessage=false`인 연속(continuation) 프레임을 `assembled` MemoryStream에 **누적 길이 검사 없이 무제한으로** 이어붙인다. 이 프로토콜의 정상 수신은 join JSON 1건(수십~수백 바이트)뿐인데, 악성/오작동 WS 클라이언트(브라우저를 거치지 않은 raw WebSocket)가 `EndOfMessage`를 세우지 않고 4KB 조각을 계속 흘려보내면 접속 1건의 MemoryStream이 무한 성장한다.
- `GET /ws`는 인증(join 수신) **이전에** accept되므로 **pre-auth로 누구나** 트리거 가능하다.
- 첫 메시지의 10초 JoinTimeout(`GameBridge.cs`)은 `WaitAsync` 상한일 뿐 `ReceiveAsync` 루프를 즉시 멈추지 않아 그 10초 동안에도 계속 누적되고, **join 이후 wsWatch 루프는 per-message 타임아웃이 없어 인증 후에는 상한이 완전히 사라진다.**
- Docker 배포는 `0.0.0.0:8081`을 호스트에 publish해 인터넷에 노출된다.
- 이는 `stress_harness_0721`에서 이미 **FAIL 판정**된 GameServer 오버사이즈 프레임 증폭(피어당 수 MB, WS 39MB→3.2GB)과 **동형 결함**이 브라우저 대면 게이트웨이에 새로 열린 것이다. 기지 제약 #3(레이트 리밋 부재)의 문서화 범위를 **넘는 신규 파생 결함**이다.

**수정:** `assembled.Length`(및 초기 단일 프레임의 `result.Count` 누적)가 하드 캡(프로토콜상 join 최대 수백 바이트이므로 4~16KB면 충분)을 넘으면 즉시 `CloseAsync(MessageTooBig, 1009)` 후 `null` 반환으로 브리지를 종료한다. `WebSocketOptions`에도 프레임/버퍼 한도를 설정한다. 효과: 접속당 수신 버퍼 메모리가 O(무제한)→O(1) 상수로 고정되어, GameServer 하드닝(IdleTimeout/프레임 레이트 리밋)과 대칭인 게이트웨이 경계 방어가 완성된다. 근본 방어는 소스 IP당 연결 레이트 리밋(기지 제약 #3, 다음 사이클)과 결합.

---

## Medium 발견사항 ← 권장 수정

### [아키텍처 + 스타일] `RunAuthenticatedSessionAsync` 단일 메서드에 5개 책임 집중 (~149줄, SRP/OCP)
**위치:** `WebClient/GameBridge.cs:108-256`
**문제:** 접속 1건 처리 메서드 하나가 ① outbound 채널 구성 ② WS 드레인 태스크 ③ TCP 접속+인증 4중 catch ④ 바이너리 패킷→JSON 번역(`OnReceived` switch) ⑤ WS 감시 루프+대칭 종료를 모두 담는다. 특히 ④ 패킷 번역은 순수 함수적 책임인데 오케스트레이션 클로저에 박혀 있어, 설계 문서 §7의 능동 입력·기여도 표시 확장 시 이 메서드를 계속 수정해야 하고 번역 로직만 단위 테스트할 수 없다(현재 실소켓 통합 테스트로만 커버).
**수정:** 패킷→JSON 번역을 무상태 클래스(`RaidPacketTranslator`, MVP 역매핑은 `GuestDirectory` 주입)로 추출 — `RaidBroadcastPackets`를 `SessionRaidRunner`에서 분리한 기존 사이클과 동일 방향. 접속/인증 블록·드레인 생성도 private 메서드로 추출하면 각 단계가 30줄 이내로 내려간다.

### [아키텍처] 게스트 수명주기 소유권 비대칭 — 등록은 Issuer, 해제는 Bridge
**위치:** `WebClient/GuestTokenIssuer.cs:70` ↔ `WebClient/GameBridge.cs:103`
**문제:** `GuestDirectory` 등록이 `GuestTokenIssuer.Issue()`의 숨은 부수효과(`_directory.Register`)로 일어나고 해제는 `GameBridge.RunAsync`의 finally(`_directory.Unregister`)가 수행한다. 하나의 수명주기가 두 클래스에 갈라져 ① `Issue()`가 이름과 달리 디렉터리 상태를 바꾸는 시간적 결합 ② 브리지가 Unregister를 잊으면 발급기 쪽에서 알 수 없는 누수 ③ 향후 실계정 로그인 탭(§7) 추가 시 각 호출부가 해제를 각자 기억해야 하는 Shotgun Surgery 구조.
**수정:** 등록/해제를 한 소유자로 모은다. 최소 수정은 `Issue()`에서 Register를 빼고 GameBridge가 인증 성공 시 Register·finally에서 Unregister를 쌍으로 수행. 또는 `GuestLease`(IDisposable 스코프)로 만들어 호출부가 쌍을 잊을 수 없게 한다.

### [아키텍처] TCP 측 이음새 부재 — `ServerNet.CreateClient()` 정적 팩토리 직접 호출 (DIP)
**위치:** `WebClient/GameBridge.cs:153`
**문제:** 브라우저 측은 `IBrowserChannel`로 추상화해 페이크를 꽂을 수 있지만 GameServer 측은 `ServerNet.CreateClient()` 정적 호출로 구체 결합돼 이음새가 비대칭이다. 오류 경로(ConnectAsync 실패, 인증 Ack 타임아웃, 송신 중 단절)를 검증하려면 항상 실제 루프백 리스너가 필요해 타임아웃·부분 실패의 결정적(deterministic) 테스트가 구조적으로 막혀 있다.
**수정:** 생성자에 `Func<IClientConnection>` 팩토리(기본값 `ServerNet.CreateClient`)를 주입해 `IBrowserChannel`과 대칭인 이음새를 만든다. 프로덕션 조립 무변경, 페이크로 인증 타임아웃·중간 단절을 밀리초 단위로 결정적 테스트 가능.

### [보안] `/ws` 핸드셰이크에 Origin 검증 없음 — Cross-Site WebSocket Hijacking (CSWSH)
**위치:** `WebClient/Program.cs` (`app.Map("/ws")`)
**CWE:** CWE-1385 (Missing Origin Validation in WebSockets), CWE-346
**문제:** `AcceptWebSocketAsync` 전에 Origin 헤더를 확인하지 않아 임의의 악성 웹페이지가 방문자 브라우저로 WebClient(:8081)에 WebSocket을 열어 join 흐름을 구동할 수 있다. 게스트 세션은 앰비언트 자격증명에 의존하지 않아 세션 탈취 피해는 제한적이나, drive-by로 방문자 IP를 이용한 게스트 세션 대량 생성 → GameServer TCP 자원 증폭(위 High와 결합)을 유발할 수 있다.
**수정:** 핸드셰이크 시 `Request.Headers.Origin`을 신뢰 호스트 허용 목록과 대조해 불일치 시 403. 배포 도메인이 고정이면 정확 일치 검사가 가장 안전.

### [보안] DEBUG 하드코딩 HMAC 폴백 시크릿을 인터넷 대면 WebClient가 소비 — 토큰 위조 위험
**위치:** `WebClient/Program.cs`, `ServerLib/Core/Auth/HmacSecretResolver.cs:66-70`, `play.bat`, `run-local.bat`
**CWE:** CWE-798 (Hard-coded Credentials), CWE-1188 (Insecure Default Initialization)
**문제:** DEBUG 빌드는 소스 평문 폴백 `'dev-only-insecure-hmac-secret-change-me'`를 사용하며, 이 시크릿은 게스트 토큰 발급과 GameServer 인증 게이트 검증에 공유된다. 값이 알려지면 임의 accountId·닉네임으로 유효 토큰을 위조해 인증 게이트를 완전 우회한다. `play.bat`/`run-local.bat`이 Debug 빌드로 두 서버를 기동해 이 폴백을 상시화한다.
**완화(존재):** Release 빌드는 시크릿을 `#else` fail-fast로 컴파일 배제, DevFallback 사용 시 콘솔 경고 출력, 시크릿은 로그 미노출. 실노출은 "Debug 빌드를 배포하거나 play.bat을 비-loopback으로 노출하는 오배포" 시나리오에 한정(그래서 medium~low 경계).
**수정:** 인터넷 노출 배포는 반드시 Release + `IDLERPG_AUTH_HMAC_SECRET`(32B+ 고엔트로피)로 기동하도록 문서·스크립트에 고정. `play.bat`이 만드는 게이트웨이가 loopback(127.0.0.1)에만 바인드됨을 유지하고, Debug 빌드 + 비-loopback 바인드 조합 시 기동 거부 가드 추가 권장.

### [스타일] 예외 삼킴 경로에 운영자 로깅 전무 — 실제 버그가 '연결 오류'로 위장
**위치:** `WebClient/GameBridge.cs:220,280,284` + `WebClient/Program.cs:69` (`ClearProviders`)
**문제:** 접속/인증 `catch (Exception)`이 모든 예외(브리지 내부 NRE 등 실제 버그 포함)를 '연결할 수 없습니다' 한 메시지로 뭉뚱그리고 예외 객체를 어디에도 남기지 않는다. `Program.cs`가 `ClearProviders()`로 ASP.NET 로그까지 껐으므로 브리지 오류의 서버 측 가시성이 0이다. 프로젝트 원칙 '조용한 은폐보다 가시적 실패'와 어긋난다.
**수정:** 브라우저 통지는 유지하되 서버 콘솔에 예외 유형을 남긴다(`Console.Error.WriteLine`). `SocketException`(서버 미기동)과 그 외(브리지 결함)를 catch 분기로 구분하면 메시지 수준에서 원인 분리 가능.

### [스타일] 실 WebSocket 어댑터·타임아웃 분기 미테스트 (39건 커버리지 밖)
**위치:** `WebClient/WebSocketBrowserChannel.cs:38-97`, `WebClient/GameBridge.cs:31-34`(하드코딩 static readonly 타임아웃)
**문제:** 39건 테스트가 전부 `FakeBrowserChannel` 경유라 `WebSocketBrowserChannel` 실어댑터(조각 조립·Binary 거부·Close 가드)와 join/auth/드레인 타임아웃 분기가 한 줄도 실행되지 않는다. **오류 통지+대칭 정리라는 이 프로젝트의 핵심 계약이 정확히 이 미커버 분기들에 있다.** (위 High 결함이 바로 이 미테스트 어댑터에 있었던 것과 무관치 않다.)
**수정:** `JoinTimeout`/`AuthTimeout`을 생성자 선택 파라미터로 승격(프로덕션 기본값 유지)해 짧게 주입 가능하게 하고, `WebApplicationFactory`/Kestrel 루프백 + `ClientWebSocket`으로 어댑터 통합 테스트 4건(4KB 초과 조립, Binary→null, 정상 Close, 취소 전파) 추가.

---

## Low / 정보성 ← 검토 권장

- [아키텍처+스타일] `Program.cs:24-33` — 포트 환경변수 파싱 실패 시 조용한 기본값 폴백(HMAC fail-fast와 비일관). "미설정=기본값, 설정했는데 오설정=fail-fast"로 통일 권장.
- [아키텍처] `GameHtml.cs`↔`BridgeMessages.cs` — JSON 계약(type 판별자·필드명)이 C#과 내장 JS에 이중 하드코딩(JS는 정적 검사 없음). 계약 스냅샷 테스트 또는 주석 앵커로 방어.
- [아키텍처] `Program.cs:24-26` — 게임 포트 기본값 7777이 GameServer/WebClient/docker-compose 3곳에 매직 넘버 중복. 배선 검증 스모크로 방어.
- [성능] `Program.cs` — `GET /` 요청마다 ~7KB HTML을 UTF-8 재인코딩(입장 시 1회, 무시 가능). `static readonly byte[]`로 1회 계산 후 `Results.Bytes` 서빙 가능.
- [성능] `GameBridge.cs:141` + `WebSocketBrowserChannel.cs:76` — 메시지당 JSON string + byte[] 이중 할당(~7 msg/s라 무해). 빈도 10배↑ 시 `Utf8JsonWriter`+`ArrayPool` 전환.
- [성능] `GameBridge.cs:157-185` — 동일 브로드캐스트를 접속 수 N만큼 중복 역직렬화·JSON 변환(게스트 1=TCP 1 설계상 구조적 한계, 현 규모 무해).
- [성능] `GameBridge.cs:126,232` — 접속당 `Task.Run` 2회 중 1회는 즉시 await하는 비동기 람다라 디스패치 1회 낭비. async 로컬 함수 직접 호출로 축소 가능.
- [성능] `GameBridge.cs:114-121` — Bounded(256) DropOldest 채널에 이벤트성(auth/error/bossDeath)과 bossHp 혼재. 256 용량 산정은 적정(36초 정체 후에야 첫 드롭), 현행 유지 가능.
- [스타일] `GameBridge.cs:114,154-155,266` + `GuestTokenIssuer.cs:84` — 이름 없는 리터럴 상수(256/10s/5s/2s/`*2`). 명명 상수로 승격해 튜닝 지점 집약.
- [스타일] `BridgeMessages.cs:37-92` — type 판별자 주입 보조 생성자 패턴 6회 반복. `init` 고정 프로퍼티(`Type => "auth"`)로 보조 생성자 제거 가능.
- [스타일] `GameBridge.cs:200-225` — '오류 통지→FinishAsync→return' 꼬리 시퀀스 4회 반복. failReason 변수로 종료 단일 경로화.
- [스타일] `GameBridgeTests.cs:93-269` — 통합 테스트 8건의 join+auth 셋업 5회 반복. `JoinAndAuthenticateAsync` 헬퍼 추출.
- [스타일] `GameHtml.cs`/`Program.cs` — 인라인 JS 상태 머신·엔드포인트 배선 미테스트. `WebApplicationFactory` 스모크 2건(`GET /` 콘텐츠 타입, `GET /ws` 비-WS→400) 추가 권장.
- [스타일] `BridgeMessages.cs:26` — public 필드명 `Json`이 역할 불명확 + mutable `JsonSerializerOptions`를 public 노출(외부가 계약 변조 가능). `SerializerOptions`로 개명 후 private 강등.
- [스타일] `FakeBrowserChannel.cs:18` — public volatile 필드 노출(캡슐화 이탈). backing 필드 + 읽기 전용 프로퍼티로 전환.
- [보안] `Program.cs`/`docker-compose.yml` — 평문 HTTP/WS 전송(CWE-319). 인터넷 노출 시 리버스 프록시 TLS 종단 뒤 배치.
- [보안] `GameHtml.cs` — 서버발 MVP 원문/닉네임 DOM 삽입 XSS 표면(**현재 `textContent`로 방어 확인됨**, CWE-79). 회귀 방지 기록 — 채팅 등 능동 입력 확장 시 HTML 인코딩 강제.

### 기지(旣知) 제약 — 이미 문서화됨 (신규 이슈 아님, `plan/web_client_0723.md §7`)
- **GuestDirectory 프로세스 로컬** (수평 확장 시 MVP 역매핑 원문 노출) — 안전하게 degrade, 파생 결함 없음 확인.
- **SessionAuthGate instanceId 포맷 강결합** (`player-{accountId}-{sessionId:N}`) — 포맷 불일치 시 파싱 실패→원문 노출로 degrade. 안전망 테스트가 WebClient.Tests에만 있어, GameServer.Tests(SessionAuthGate 쪽)에도 포맷 재현 테스트 1건 두면 변경자가 즉시 깨짐을 본다(개선 제안).
- **`/ws` 접속·join 레이트 리밋 부재** — GameServer 하드닝 토글(MAX_CONNECTIONS/IDLE_TIMEOUT)이 후방 방어선. 위 High 결함과 결합 시 증폭되는 점이 이번 리뷰의 추가 근거.

---

## 총평 및 판정

WebClient 신설은 **레이어 격리(ServerLib만 참조)·`IBrowserChannel` 이음새·접속별 상태 지역화·종료 수순 대칭·프로젝트 특수 주석 규칙 완전 준수** 등 구조적 기반이 견고하고, 설계 문서가 기지 제약 3건을 정직하게 문서화한 점도 높이 평가된다. 그러나 인터넷 대면 게이트웨이라는 성격상 **머지 전 반드시 고쳐야 할 High 결함 1건**이 있다: `WebSocketBrowserChannel`의 조각 프레임 조립에 크기 상한이 없어 pre-auth로 트리거 가능한 메모리 증폭 DoS가 열려 있으며, 이는 스트레스 하네스가 GameServer에서 이미 FAIL로 판정한 결함과 동형이다. 보안·성능·스타일 3개 도메인이 독립적으로 같은 파일을 지목한 것이 그 심각성을 뒷받침한다. Medium 레벨에서는 `GameBridge`의 5책임 집중(149줄)과 예외 삼킴 시 운영 가시성 0, 실어댑터·타임아웃 분기 미테스트가 함께 해소되어야 유지보수성과 방어가 완성된다.

**판정: REQUEST CHANGES** (종합 77점, High 결함 존재)

- High 1건(WS 무제한 버퍼)을 크기 캡 + 미테스트 어댑터 통합 테스트로 해소하면 보안·성능 점수가 크게 오르고 사실상 APPROVE 수준에 도달한다.
- Origin 검증·예외 로깅·`GameBridge` 책임 분리는 후속 사이클에서 함께 처리 권장(과거 관례: 리뷰 → 후속 수정 워크로그 분리).
