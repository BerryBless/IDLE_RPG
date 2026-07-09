# 로그인 구현 (별도 AuthServer + MongoDB + 더미 3000 로그인 테스트)

작성일: 2026-07-09

## 1. 배경 및 목적

`GameServer`는 아직 로그인이 없다. 소켓 연결 시 `SessionPlayerBinder.OnConnected`가
`PlayerFactory.CreateTemp(...)`로 임시 `Player`(항상 `AccountId=0`)를 붙일 뿐이며, 이 때문에
`Main.cs`는 아무나 무제한 임시 플레이어를 만들 수 없도록 `IPAddress.Loopback`에만 바인딩돼 있다.

이번 작업의 목적은 자격증명 기반 로그인을 도입하는 것이다: (1) 로그인 책임을 게임 로직과
분리한 **별도 AuthServer 프로세스**로 구현하고, (2) 저장소는 **MongoDB(NoSQL)**를 쓰되
`IAccountRepository`로 추상화해 테스트는 **인메모리 페이크**로 실행하며, (3) **더미 계정
3000개**를 시딩해 **정확성**(맞는 자격증명=성공, 틀린 비밀번호/없는 계정=실패)을 검증하고,
(4) 비밀번호는 **PBKDF2 해시**로 저장한다.

`ServerLib`에는 이미 `LoginRequestPacket`(Id=10)·`LoginResponsePacket`(Id=11)·
`AuthTokenPacket`(Id=12)이 정의만 된 채 배선되지 않은 상태로 존재했고, `SessionState.Authenticated`·
`Player.AccountId`도 로그인 도입을 위한 예비 seam으로 이미 있었다. 이번 사이클은 이 자산들을
재사용해 AuthServer 쪽 배선을 완성한다. GameServer가 발급된 토큰으로 인증 게이트를 통과시키는
배선(§7 다음 사이클)은 이번 범위 밖이다.

## 2. 설계 결정

| 항목 | 채택 | 대안 | 사유 |
|------|------|------|------|
| 프로세스 구조 | **별도 `AuthServer` 프로세스** | GameServer에 로그인 핸들러 내장 | 이미 정의된 3-패킷(Login→Token→Auth) 구조가 클라-Auth-Game 분리를 전제하고, 인증 책임을 게임 로직에서 격리해 다음 사이클에서 GameServer는 토큰 검증만 하면 되도록(무상태) 한다 |
| 저장소 | **MongoDB + `IAccountRepository` 추상화** | 관계형 DB, 파일 기반 저장 | 사용자 지정 요구사항(NoSQL/MongoDB). 인터페이스로 감싸 테스트가 실제 DB에 의존하지 않게 한다 |
| 테스트용 저장소 | **인메모리 `ConcurrentDictionary` 페이크** | Testcontainers/Mongo2Go 임베디드 Mongo | 3000건 정확성 검증은 로직 검증이 핵심이라 실제 Mongo 프로토콜을 띄울 필요가 없다. 추가 인프라 의존(Docker 등) 없이 빠르고 결정적으로 실행 |
| 3000 더미의 목적 | **정확성 검증**(성공/실패 판정) | 동시성·부하 테스트 | 사용자 확인: 목적은 "맞는 자격=성공, 틀린 자격=실패"이지 처리량 측정이 아님 |
| 비밀번호 저장 | **PBKDF2(HMAC-SHA256) 해시, 반복 횟수 주입 가능** | BCrypt.Net-Next, 평문 | .NET 내장 `Rfc2898DeriveBytes`만으로 외부 의존성 없이 구현 가능. 반복 횟수를 생성자 인자로 열어 두어 운영(100,000)과 테스트(1,000, sub-second)를 분리 |
| 토큰 발급 방식 | **무상태 HMAC-SHA256 서명 토큰** (`ServerLib/Core/Auth`) | DB에 세션 저장 후 조회 | AuthTokenPacket을 다음 사이클에 GameServer가 검증할 때 AuthServer와 DB를 공유할 필요 없이 서명만으로 검증 가능. 두 서버가 이미 `ServerLib`를 참조하므로 새 프로젝트 없이 코덱을 공유 |
| 코덱 배치 위치 | **`ServerLib/Core/Auth`** | 별도 `IdleRpg.Auth` 공유 라이브러리 | 로그인 DTO가 이미 ServerLib에 있고 AuthServer/GameServer 모두 ServerLib을 참조하므로 새 프로젝트 없이 공유 가능. `MongoDB.Driver`는 절대 ServerLib에 반입하지 않고 AuthServer exe에만 국한 |
| 수신 패킷 처리 경로 | **`OnReceived` + `BinaryPacketSerializer.Deserialize<T>(data.Span)` 직접 호출** | `RpcDispatcher` 경유 | 코드 확인 결과 `Deserialize<T>`는 헤더 포함 전체 패킷을 받아 내부에서 4B를 스킵하므로, `RpcDispatcher`(payload 기대, 2B만 스킵)와 오프셋 계약이 다르다. `EchoEndToEndTests`와 동일한 검증된 경로를 그대로 사용 |

## 3. 컴포넌트 구조

```
IDLE_RPG/
├─ AuthServer/                              ← 신규 (GameServer와 동급 실행 파일)
│   ├─ AuthServer.csproj                    (net10.0 Exe, ServerLib 참조, MongoDB.Driver 3.10.0)
│   ├─ Program.cs                           (호스트 배선 + --seed/--force CLI)
│   ├─ Configuration/AuthServerConfig.cs     (env var + 기본값)
│   ├─ Accounts/
│   │   ├─ Account.cs                       (BSON 매핑 도메인 모델)
│   │   ├─ IAccountRepository.cs
│   │   └─ MongoAccountRepository.cs        (운영 구현 + EnsureIndexesAsync/DropAllAsync)
│   ├─ Security/
│   │   ├─ IPasswordHasher.cs
│   │   └─ Pbkdf2PasswordHasher.cs
│   ├─ Login/
│   │   ├─ LoginResult.cs
│   │   ├─ LoginService.cs
│   │   └─ AuthConnectionHandler.cs         (OnReceived 배선 단일 출처)
│   └─ Seeding/AccountSeeder.cs             (결정적 더미 생성, 임의 IAccountRepository 대상)
├─ ServerLib/
│   └─ Core/Auth/                           ← 신규 (AuthServer/GameServer 공유 코덱)
│       ├─ IAuthTokenIssuer.cs
│       ├─ IAuthTokenValidator.cs
│       ├─ AuthTokenClaims.cs
│       └─ HmacAuthTokenCodec.cs
├─ tests/AuthServer.Tests/                  ← 신규
│   ├─ AuthServer.Tests.csproj
│   ├─ InMemoryAccountRepository.cs         (테스트 전용 페이크)
│   ├─ HmacTokenCodecTests.cs
│   ├─ PasswordHasherTests.cs
│   ├─ AccountCorrectnessTests.cs           (3000 더미 정확성 검증)
│   ├─ LoginPacketRoundTripTests.cs         (기존 패킷 특성화 테스트)
│   └─ AuthServerEndToEndTests.cs           (루프백 실소켓 스모크)
└─ IDLE_RPG.sln                             (AuthServer·AuthServer.Tests 등록)
```

의존 관계:
```
ServerLib (기존 + Core/Auth 신규)
   ↑ ProjectReference
   ├─ AuthServer            ← 신규: LoginService가 IAccountRepository·IPasswordHasher·
   │                            IAuthTokenIssuer(HmacAuthTokenCodec)를 조합
   │     ↑ ProjectReference
   │     └─ tests/AuthServer.Tests   (InMemoryAccountRepository는 여기에만 존재)
   └─ GameServer             (기존, 이번 사이클 미변경 — 다음 사이클에서 코덱 공유 예정)
```

## 4. 핵심 API

```csharp
// 토큰 발급/검증 (ServerLib.Core.Auth) — 두 서버가 공유
var codec = new HmacAuthTokenCodec(secretBytes);
string token = codec.Issue(accountId: 42, username: "alice", DateTimeOffset.UtcNow.AddHours(1));
bool ok = codec.TryValidate(token, out AuthTokenClaims claims); // DB 조회 없이 서명만으로 검증

// 로그인 유스케이스 (AuthServer.Login)
var login = new LoginService(repository, hasher, codec, tokenLifetime: TimeSpan.FromHours(1));
LoginResult result = await login.AuthenticateAsync(username, password);
// result.Success / result.Token (실패 시 빈 문자열)

// 결정적 더미 시딩 — 테스트와 실 MongoDB가 동일 로직 재사용
await AccountSeeder.SeedAsync(repository, hasher, count: 3000);
// 계정 i → Username = AccountSeeder.UsernameFor(i) (예: "user0000")
//         Password = AccountSeeder.PasswordFor(i)  (예: "Pass!0000")

// 서버 배선 (AuthServer/Program.cs) — EchoServer/GameServer와 동일한 ServerNet 관례
var handler = new AuthConnectionHandler(login, new BinaryPacketSerializer());
IServerListener listener = ServerNet.CreateListener();
listener.OnReceived = handler.OnReceived;   // LoginRequestPacket(Id=10)만 처리, 나머지는 무시
listener.Start(AuthServerConfig.Port, IPAddress.Loopback);
```

## 5. 변경 파일 목록

**신규 — AuthServer 실행 파일**
| 파일 | 내용 |
|------|------|
| `AuthServer/AuthServer.csproj` | net10.0 Exe, ServerLib 참조, `MongoDB.Driver 3.10.0`, `InternalsVisibleTo AuthServer.Tests` |
| `AuthServer/Program.cs` | 리스너 배선 + `--seed`/`--seed --force` CLI 처리 |
| `AuthServer/Configuration/AuthServerConfig.cs` | `IDLERPG_MONGO_CONN`/`IDLERPG_MONGO_DB`/`IDLERPG_AUTH_PORT`/`IDLERPG_AUTH_HMAC_SECRET` env var + 기본값 |
| `AuthServer/Accounts/Account.cs` | BSON 매핑 계정 모델 |
| `AuthServer/Accounts/IAccountRepository.cs` | 저장소 계약 |
| `AuthServer/Accounts/MongoAccountRepository.cs` | 운영 구현(`EnsureIndexesAsync`/`DropAllAsync` 포함) |
| `AuthServer/Security/IPasswordHasher.cs` | 해셔 계약 |
| `AuthServer/Security/Pbkdf2PasswordHasher.cs` | PBKDF2-HMAC-SHA256 구현, 반복 횟수 주입 가능 |
| `AuthServer/Login/LoginResult.cs` | 성공/실패 결과 구조체 |
| `AuthServer/Login/LoginService.cs` | 자격 검증 → 토큰 발급 |
| `AuthServer/Login/AuthConnectionHandler.cs` | `OnReceived` 핸들러(예외 시 실패 응답으로 정리) |
| `AuthServer/Seeding/AccountSeeder.cs` | 결정적 더미 생성(`UsernameFor`/`PasswordFor`/`SeedAsync`) |

**신규 — ServerLib 공유 코덱**
| 파일 | 내용 |
|------|------|
| `ServerLib/Core/Auth/IAuthTokenIssuer.cs`, `IAuthTokenValidator.cs`, `AuthTokenClaims.cs`, `HmacAuthTokenCodec.cs` | 무상태 HMAC-SHA256 토큰 발급/검증 |

**신규 — 테스트**
| 파일 | 내용 |
|------|------|
| `tests/AuthServer.Tests/AuthServer.Tests.csproj` | xUnit, AuthServer+ServerLib 참조 |
| `InMemoryAccountRepository.cs` | 테스트 전용 페이크(운영 바이너리에 미포함) |
| `HmacTokenCodecTests.cs` (7) | 발급/검증 왕복, 위변조, 만료, 잘못된 secret, `\|` 포함 username |
| `PasswordHasherTests.cs` (6) | 해시/검증 왕복, 오답 거부, salt 무작위성, 변조 거부, 운영 기본값(100k) 왕복 |
| `AccountCorrectnessTests.cs` (8) | 3000 더미 시딩 후 정확/오답/미존재 계정 검증 |
| `LoginPacketRoundTripTests.cs` (13) | 기존 Login/Response/AuthToken 패킷 특성화(신규 버그 없음 확인) |
| `AuthServerEndToEndTests.cs` (2) | 루프백 실소켓: 정답/오답 로그인 왕복 |

**수정**
| 파일 | 내용 |
|------|------|
| `IDLE_RPG.sln` | `AuthServer`·`AuthServer.Tests` 프로젝트 등록(`dotnet sln add`로 GUID 생성, `tests` 솔루션 폴더에 자동 중첩) |

GameServer 소스는 이번 사이클에서 변경하지 않았다(기존 패킷을 그대로 소비할 준비만 된 상태).

## 6. 빌드 검증

```bash
dotnet build IDLE_RPG.sln -c Debug              # 0 경고, 0 오류
dotnet test tests/AuthServer.Tests/AuthServer.Tests.csproj   # 36/36 통과
dotnet test IDLE_RPG.sln                        # 신규 스위트 포함 전체 회귀 그린 (기존 EchoExample.Tests 13개,
                                                 # GameServer.Tests 154개 회귀 없음)
dotnet run --project AuthServer -- --seed       # 실 mongod 필요 — 3000 더미를 실제 MongoDB에 시딩
```

`MongoDB.Driver`는 최초 `3.1.0`으로 추가했으나 전이 의존성(`SharpCompress`/`Snappier`)에 알려진
취약점 경고(NU1902/NU1903)가 있어 `3.10.0`으로 올려 경고 0건으로 정리했다.

`IdleRpg.HarnessTests`에서 3건의 실패(에이전트 매니페스트 드리프트: `worklog-writer` 고아 에이전트,
`general-purpose` 미등록 참조, 에이전트 수 불일치)가 관측되나, 이번 세션에서 `.claude/` 하위를
전혀 건드리지 않았고(`git status`로 변경 파일 확인) 해당 파일들의 마지막 커밋(`3b707c6`)도 이전
사이클(워크로그 하네스 신설)에서 발생한 것으로 확인되어 이번 로그인 작업과 무관한 기존 이슈다.

## 7. 향후 확장 포인트

- **GameServer 토큰 게이트 배선(다음 사이클 최우선 과제)**: `Main.cs`에서 `AuthTokenPacket(Id=12)`
  수신 → `HmacAuthTokenCodec.TryValidate` → 성공 시 `session.TransitionTo(SessionState.Authenticated)`
  → `PlayerFactory.Create(instanceId, claims.AccountId, level, levelSystem)`로 실제 `Player` 결합
  (`CreateTemp`/`AccountId=0` 대체). 미인증 세션의 다른 패킷 거부, `IPAddress.Loopback`→`Any`
  재검토(TLS 선행 필요)까지 포함.
- **TLS 도입**: `LoginRequestPacket.Password`는 평문 전송이라 현재 AuthServer도 GameServer와
  동일하게 Loopback 전용으로만 노출한다. 외부 공개 전 TLS(또는 최소 전송 레이어 암호화)가 선행돼야 한다.
- **회원가입(계정 생성) API**: 이번 사이클은 시더(`--seed`)로만 계정을 만든다. 실제 서비스라면
  `RegisterRequestPacket` 같은 신규 패킷과 사용자 이름 중복 검사 흐름이 필요하다.
  `MongoAccountRepository`의 unique index가 이미 준비되어 있어 자연스럽게 확장 가능.
  `LoginService.AuthenticateAsync`처럼 `IAccountRepository`/`IPasswordHasher`를 그대로 재사용 가능.
  `InsertAsync` 단건 삽입 경로도 이미 존재.
- **타이밍 사이드채널(계정 열거) 하드닝**: 존재하지 않는 계정 경로가 PBKDF2 검증을 건너뛰어
  응답이 더 빠르다. TLS 도입 이후 더미 `Verify` 호출로 시간을 평탄화하는 방안을 재검토.
- **HMAC secret 로테이션**: 현재는 고정 비밀키 1개(`IDLERPG_AUTH_HMAC_SECRET`)만 지원한다.
  키 로테이션이 필요해지면 `AuthTokenClaims`에 키 버전 필드를 추가하고 `HmacAuthTokenCodec`이
  여러 키를 순차 검증하도록 확장해야 한다.
- **실 MongoDB 통합 테스트**: 이번 사이클은 인메모리 페이크로만 검증했다. CI에 Docker/
  Testcontainers 기반 실 Mongo 인스턴스를 추가하면 `MongoAccountRepository`의 인덱스·쿼리
  동작까지 자동 회귀 검증할 수 있다.
