# GameServer 토큰 게이트 배선

작성일: 2026-07-09

## 1. 배경 및 목적

`login_mongo_0709.md`에서 별도 `AuthServer` 프로세스가 로그인·HMAC 토큰 발급까지 완성했지만,
`GameServer`는 여전히 로그인이 없다. 소켓 연결 시 `SessionPlayerBinder.OnConnected`가
`PlayerFactory.CreateTemp(...)`로 임시 `Player`(항상 `AccountId=0`)를 즉시 만들어 붙이고,
곧바로 공유 레이드 보스 전투에 자동 참전시킨다 — 클라이언트가 패킷을 하나도 보내지 않아도
게임이 진행되는 완전 자동 틱 구조다.

이번 작업의 목적은 AuthServer가 발급한 토큰(`AuthTokenPacket`, Id=12)을 GameServer가 검증해
**실제 계정으로 인증된 세션만 레이드에 참전**하도록 강한 관문(gate)을 두는 것이다. 인증에 성공하기
전까지는 `Player`를 만들지도, 레이드 제출 루프를 시작하지도 않는다.

## 2. 설계 결정

| 항목 | 채택 | 대안 | 사유 |
|------|------|------|------|
| 게이트 강도 | **강한 관문**(인증 성공 전 Player 없음, 레이드 미참전) | 약한 업그레이드(접속 즉시 임시 참전, 토큰 도착 시 AccountId만 교체) | 사용자 확인. `login_mongo_0709.md` §7의 원래 방향과 일치 |
| 검증 결과 통지 | **신규 `AuthTokenAckPacket`(Id=18, `bool Success`) 추가** | 무응답(클라가 간접 추론) | 사용자 확인. `LoginResponsePacket`과 대칭을 이루는 명시적 성공/실패 신호 |
| 기존 임시 경로 처리 | **`PlayerFactory.CreateTemp`/`SessionPlayerBinder.OnConnected` 삭제하지 않고 Main.cs 배선만 제거** | 완전 삭제 후 연쇄 호출부 정리 | 두 메서드가 레이드/보상/구버전 `SessionBattleRunner` 테스트 4곳에서 "플레이어를 빠르게 만드는 픽스처 헬퍼"로 쓰이고 있어, 삭제하면 이번 사이클과 무관한 테스트까지 건드리는 범위 확장이 된다(CLAUDE.md "불필요한 리팩토링 금지" 원칙). 문서 주석만 갱신해 드리프트 방지 |
| Player instanceId 체계 | **`$"player-{accountId}-{sessionId:N}"`** | `$"player-{accountId}"` | 같은 계정이 두 세션으로 동시 접속하면 순수 accountId 기반 instanceId는 `RaidRewardApplier`의 InstanceId 키 충돌(보상 오배송)을 일으킨다. 세션ID를 섞어 세션마다 고유성을 보장하면서도 실제 계정으로 추적 가능하게 유지 |
| GameServer의 시크릿 취득 | **`IDLERPG_AUTH_HMAC_SECRET` 환경변수 인라인 읽기**(Main.cs) | 별도 `GameServerConfig` 클래스 | 필요한 설정값이 1개뿐이라 `Port=7777`과 동일한 인라인 상수 스타일 유지. AuthServer의 `AuthServerConfig.HmacSecret` 기본값과 동일 문자열을 써서 아무 환경변수 없이도 로컬에서 바로 맞물리게 함 |
| 미인증 세션 타임아웃 | **없음(이번 사이클 범위 밖)** | IdleTimeout/수동 타이머로 강제 종료 | GameServer에 하트비트/핑 프로토콜이 아직 없다는 기존 결정(Main.cs 주석)을 뒤집지 않음. 미인증 세션은 Player가 없어 리소스 비용이 없으므로 방치해도 안전 |

## 3. 컴포넌트 구조

```
GameServer/
├─ Systems/
│   ├─ SessionAuthGate.cs        ← 신규: AuthTokenPacket 검증 → Player 결합 → Ack 응답
│   ├─ SessionPlayerBinder.cs    (기존, OnConnected는 더 이상 Main.cs가 호출하지 않음 — 문서만 갱신)
│   ├─ PlayerFactory.cs          (기존, CreateTemp 문서만 갱신 — 시그니처/동작 변경 없음)
│   └─ SessionRaidRunner.cs      (기존, 변경 없음 — OnConnected 호출 시점만 바뀜)
└─ Main.cs                       (수정: OnReceived 신규 배선, OnClientConnected에서 binder 제거)

ServerLib/Core/Serialization/Packets/
└─ AuthTokenAckPacket.cs         ← 신규: Id=18, struct, bool Success

tests/GameServer.Tests/Systems/
├─ AuthTokenAckPacketRoundTripTests.cs   ← 신규
├─ SessionAuthGateTests.cs               ← 신규 (FakeSession 기반 단위 테스트)
├─ SessionAuthGateEndToEndTests.cs       ← 신규 (실 루프백 소켓 스모크)
└─ SessionConnectionEndToEndTests.cs     (문서 주석만 정정, 테스트 로직 변경 없음)
```

의존 관계: `SessionAuthGate`는 `ServerLib.Core.Auth.IAuthTokenValidator`(GameServer는 검증만 필요,
발급은 AuthServer 전용)와 `PlayerFactory.Create`(기존 정식 생성 경로)만 사용한다. `AuthServer`
프로젝트에 대한 참조는 추가하지 않는다 — 공유 지점은 이미 `ServerLib`뿐이다.

## 4. 핵심 API

```csharp
// Main.cs 배선
var hmacSecret = Encoding.UTF8.GetBytes(
    Environment.GetEnvironmentVariable("IDLERPG_AUTH_HMAC_SECRET") ?? "dev-only-insecure-hmac-secret-change-me");
var authValidator = new HmacAuthTokenCodec(hmacSecret);
var authGate = new SessionAuthGate(authValidator, levelSystem, sink, new BinaryPacketSerializer());

listener.OnReceived = async (session, data) =>
{
    bool justAuthenticated = await authGate.HandleAsync(session, data);
    if (justAuthenticated)
        await raidRunner.OnConnected(session); // 인증 성공 시에만 레이드 참전 시작
};

// OnClientConnected에서 binder.OnConnected 호출은 제거(더 이상 접속 즉시 임시 플레이어 없음)
listener.OnClientDisconnected = async session =>
{
    await raidRunner.OnDisconnected(session);
    await binder.OnDisconnected(session); // 변경 없음 — TryGetContext로 이미 방어적
};
listener.OnClientError = binder.OnError; // 변경 없음
```

## 5. 변경 파일 목록

**신규**
| 파일 | 내용 |
|------|------|
| `GameServer/Systems/SessionAuthGate.cs` | `AuthTokenPacket` 검증 → 실 `Player` 결합 → `AuthTokenAckPacket` 응답 |
| `ServerLib/Core/Serialization/Packets/AuthTokenAckPacket.cs` | Id=18, `bool Success`(struct, zero-alloc) |
| `tests/GameServer.Tests/Systems/AuthTokenAckPacketRoundTripTests.cs` | 직렬화 왕복 + PacketId 계약 |
| `tests/GameServer.Tests/Systems/SessionAuthGateTests.cs` | FakeSession 기반: 정상 토큰/위조 토큰/만료/중복 인증/무관 패킷 무시 |
| `tests/GameServer.Tests/Systems/SessionAuthGateEndToEndTests.cs` | 실 루프백 소켓: 정상 로그인 → Ack 성공 → 레이드 참전, 오답 → Ack 실패 |

**수정**
| 파일 | 내용 |
|------|------|
| `GameServer/Main.cs` | HMAC 시크릿 취득 + `SessionAuthGate` 구성, `OnReceived`/`OnClientConnected` 배선 변경 |
| `GameServer/Systems/SessionPlayerBinder.cs` | `OnConnected` 문서 주석에 "Main.cs 미사용, SessionAuthGate가 대체" 명시(로직 변경 없음) |
| `GameServer/Systems/PlayerFactory.cs` | `CreateTemp` 문서 주석에 동일 취지 추가(로직 변경 없음) |
| `tests/GameServer.Tests/Systems/SessionConnectionEndToEndTests.cs` | 클래스 문서 주석만 "Main.cs 배선 재현" → "SessionPlayerBinder 자체 계약 테스트"로 정정 |

## 6. 빌드 검증

```bash
dotnet build IDLE_RPG.sln -c Debug
dotnet test tests/GameServer.Tests/GameServer.Tests.csproj
dotnet test IDLE_RPG.sln   # AuthServer.Tests(36)·EchoExample.Tests(13) 등 기존 스위트 회귀 없어야 함
```

## 7. 향후 확장 포인트

- 미인증 세션 타임아웃/강제 종료(하트비트 프로토콜 도입과 함께)
- 동시 다중 로그인(같은 계정, 여러 세션) 정책 — 현재는 세션마다 독립 허용
- 회원가입 플로우, TLS 도입(§`login_mongo_0709.md` §7과 공통)
- `IPAddress.Any` 전환은 TLS 선행 필요(변경 없음)
