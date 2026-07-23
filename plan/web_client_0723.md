# 브라우저 게스트 플레이 게이트웨이 (WebClient) — 2026-07-23

## 1. 배경 및 목적

지금까지 GameServer의 공유 보스 레이드는 LoadTester(콘솔)나 테스트 코드로만 "플레이"할 수
있었고, 사람이 직접 참전해 볼 수 있는 클라이언트가 없었다. 이 사이클은 **브라우저에서 직접
플레이 가능한 웹 클라이언트**를 추가한다 — 게스트 로그인(닉네임만 입력)으로 입장하면 내
캐릭터가 공유 레이드 보스(7001)에 자동 참전하고, 보스 HP바·처치/MVP 이벤트를 실시간으로 본다.

제약 두 가지가 설계를 결정했다:

1. **브라우저는 raw TCP를 열 수 없다** — GameServer(7777)는 커스텀 바이너리 TCP 프로토콜이라
   중간에 WebSocket↔TCP 브리지가 필요하다.
2. **현 전투 모델은 완전 서버 구동**이다 — 클라이언트가 보내는 앱 패킷은 `AuthTokenPacket` 1개뿐
   이고(공격 입력 패킷 자체가 없음), 인증 후엔 서버가 500ms 틱으로 대신 싸운다. 따라서 웹
   클라이언트는 "참전 + 관전" UI만으로 완결되며 **GameServer/ServerLib는 무변경**이다.

## 2. 설계 결정

| 결정 | 채택 | 기각 대안 | 근거 |
|------|------|-----------|------|
| 배치 | 신규 `WebClient` 프로세스 | MonitorServer에 통합 | 관전(모니터)과 플레이(브리지) 책임 분리. 웹 의존성 격리 원칙은 동일하게 유지(ServerLib만 참조) |
| 브라우저↔게이트웨이 프로토콜 | **JSON 텍스트 프레임** | 바이너리 패스스루(WS binary로 TCP 프레임 그대로 중계) | ① 게스트 토큰 발급(HMAC 시크릿)이 서버 측에만 있어야 해 순수 패스스루가 성립 불가 ② MVP instanceId→닉네임 역매핑 같은 의미 보강 불가 ③ JS에 바이너리 파서 중복 구현·디버깅 불가 ④ 트래픽 ~7msg/s(MobHp 150ms 스로틀)라 JSON 오버헤드 무의미 |
| 게스트 인증 | WebClient가 `HmacAuthTokenCodec.Issue()`로 직접 발급 | AuthServer 경유 게스트 계정 | LoadTester `--mode game`(`LocalHmacTokenSource`)과 동일 원리 — AuthServer/Mongo 없이 동작. GameServer 게이트는 서명·만료만 검증하므로 발급자가 시크릿만 공유하면 됨 |
| 게스트 accountId | `Interlocked.Increment`, **1_000_000부터** | 음수 대역 | 시딩 계정(0..2999)과 분리. 양수 유지로 로그·instanceId 가독성 확보 |
| WS 송신 경로 | `Channel<string>`(Bounded 256, **DropOldest**) + 단일 드레인 태스크 | OnReceived에서 직접 WS 송신 | WebSocket은 송신 동시 1호출 계약 + 느린 브라우저가 ServerLib I/O 스레드를 붙잡으면 수신 루프 정체. HP바는 최신값만 의미 있어 백로그 시 오래된 것부터 버리는 게 정당 |

### MVP 닉네임 역매핑 (핵심 의존점)

`MobDeathPacket.MvpName`은 닉네임이 아니라 **`player-{accountId}-{sessionId:N}` 형식의 서버 내부
instanceId**로 온다(`GameServer/Systems/SessionAuthGate.cs:98` — 동시 다중 로그인 보상 오배송
방지를 위해 세션ID를 섞는 설계). GameServer 무변경 원칙하에서 브라우저에 사람이 읽을 이름을
보여주려면 WebClient가 접두사에서 accountId를 파싱해 자체 발급 디렉터리(`GuestDirectory`)로
역매핑해야 한다. **SessionAuthGate의 instanceId 포맷이 바뀌면 `GuestDirectory.TryResolveMvp`도
함께 갱신해야 하며**, 이 의존은 양쪽 주석과 `GuestDirectoryTests`로 고정했다. 다른 프로세스
(다른 WebClient 인스턴스·LoadTester)의 플레이어가 MVP면 역매핑이 실패하고 instanceId 원문을
그대로 노출한다(수평 확장 시 공유 저장소로 승격 — §7).

## 3. 컴포넌트 구조

```
WebClient/                        (Microsoft.NET.Sdk.Web, ServerLib만 참조)
├─ Program.cs                     환경변수 → 시크릿 해석(fail-fast) → Kestrel + UseWebSockets → GET /, /ws
├─ GameHtml.cs                    단일 페이지(입장/전투 2화면, 인라인 CSS/JS, 외부 CDN 0)
├─ BridgeMessages.cs              JSON DTO(join/joined/auth/bossHp/bossDeath/status/error) + camelCase 옵션
├─ GuestTokenIssuer.cs            닉네임 정제(Guest-XXXX 폴백) + accountId 할당 + HMAC 토큰 발급
├─ GuestDirectory.cs              accountId→닉네임 (ConcurrentDictionary) + MVP instanceId 역매핑
├─ IBrowserChannel.cs             WS 추상화(테스트 이음새)
├─ WebSocketBrowserChannel.cs     실 WebSocket 어댑터(조각 프레임 조립 포함)
└─ GameBridge.cs                  접속 1건 브리지: join→토큰→TCP인증→중계→대칭 종료
```

데이터 흐름(접속 1건):

```
브라우저 ──WS(JSON)── WebClient ──TCP(바이너리)── GameServer:7777
  join ─────────────▶ 토큰 발급 ──AuthTokenPacket──▶ SessionAuthGate
  joined/auth ◀────── AuthTokenAckPacket ◀───────────┘
  bossHp/bossDeath ◀─ Channel(256,DropOldest) ◀─ MobHpPacket/MobDeathPacket (registry.BroadcastAsync)
```

**생명주기 대칭:** `/ws` 델리게이트가 소유. `linked CTS(RequestAborted+셧다운)` → 브리지 내부
`session CTS`로 연쇄. WS 끊김(수신 루프 null)·TCP 끊김(`OnDisconnected` TCS)·셧다운 중 어느 것이
먼저든: 드레인 태스크 종료 → TCP는 `await using DisposeAsync` → WS는 `status:disconnected` 송신
시도 후 Close → `GuestDirectory` 해제.

## 4. 핵심 API

```csharp
// 게스트 토큰 발급(AuthServer 불필요 — GameServer와 시크릿만 공유)
var directory = new GuestDirectory();
var issuer = new GuestTokenIssuer(new HmacAuthTokenCodec(hmacSecret), directory, TimeSpan.FromMinutes(10));
GuestIdentity guest = issuer.Issue("닉네임");   // → AccountId(1_000_000+), 정제 닉네임, HMAC 토큰

// 브리지: 브라우저 채널 1개를 GameServer TCP 1개로 끝까지 중계(예외 없음, 반환 = 완전 정리)
var bridge = new GameBridge(issuer, directory, gameHost, gamePort);
await bridge.RunAsync(new WebSocketBrowserChannel(socket), linkedToken);

// MVP 역매핑(MobDeathPacket.MvpName = "player-{accountId}-{sessionId:N}")
if (directory.TryResolveMvp(death.MvpName, out int accountId, out string nickname)) { ... }
```

## 5. 변경 파일 목록

| 파일 | 변경 |
|------|------|
| `WebClient/*` (8개) | 신규 — 위 §3 구조 |
| `tests/WebClient.Tests/*` (6개) | 신규 — 단위 27건(발급기·디렉터리·JSON 계약) + 통합 8건(실 루프백 리스너 + 프로덕션 `SessionAuthGate` 조합: 인증 중계·브로드캐스트 번역·MVP 역매핑·양방향 단절 대칭 정리·시크릿 불일치 거부·프로토콜 위반 거부) + 페이크 채널 |
| `IDLE_RPG.sln` | WebClient·WebClient.Tests 등록 |
| `docker-compose.yml` | `webclient` 서비스 추가(aspnet 이미지, 8081 publish, 시크릿 gameserver와 동일 주입) |
| `run-local.bat` | WebClient 창 추가(4번째) |
| `CLAUDE.md` | 프로젝트 요약·플랜 목록 갱신 |

## 6. 빌드 검증

```
dotnet build IDLE_RPG.sln
dotnet test tests/WebClient.Tests          # 39/39
dotnet test IDLE_RPG.sln                   # 기존 스위트 회귀 0 (HarnessTests 기실패 3건은 무관한 .claude 메타 드리프트)

# 수동 E2E
dotnet run --project GameServer
dotnet run --project WebClient             # → http://127.0.0.1:8081 브라우저 2탭 입장
```

## 7. 향후 확장 포인트

- **능동 입력(공격 버튼·스킬)**: GameServer에 클라이언트 입력 패킷 신설 필요 — 브리지는 join 외
  후속 WS 메시지를 현재 무시하므로 `GameBridge`의 WS 감시 루프에 분기만 추가하면 됨.
- **실계정 로그인 탭**: AuthServer(7778) 경유 `LoginRequestPacket` 경로를 브리지에 추가(게스트와
  동일한 `AuthTokenPacket` 제출로 수렴).
- **내 기여도/딜량 표시**: 현재 서버가 개인별 스탯을 푸시하지 않음 — 플레이어별 상세 텔레메트리
  (plan/web_monitoring_0718.md의 명시 제외 항목)와 함께 설계 필요.
- **WebClient 수평 확장**: `GuestDirectory`가 프로세스 로컬이라 인스턴스 2개부터는 상대 인스턴스
  게스트의 MVP가 instanceId 원문으로 보인다 — 공유 저장소(또는 accountId 대역 분할+네이밍 규약)로 승격.
- **레이트 리밋**: /ws 접속·join 시도에 IP당 상한(현재는 GameServer 쪽 하드닝 토글에 의존).
