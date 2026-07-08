# 전투 멀티플레이 2단계: 공유 보스 co-op

작성일: 2026-07-08

## 1. 배경 및 목적

지난 사이클(`plan/battle_multiplayer_0708.md`)에서 접속한 각 세션이 자신만의 독립 몬스터를 서버
자동 틱으로 사냥하도록 만들었다. 이번 사이클은 사용자가 처음부터 원한다고 확정한 다음 단계다:
**접속한 모든 플레이어가 하나의 공유 레이드 보스를 동시에 공격**하고, 보스 HP를 전원에게
브로드캐스트하며, 처치 시 기여도 비례로 보상을 나눈다.

핵심은 이미 존재하되 아무도 구동하지 않던 두 가지를 살려 배선하는 것이었다:
1. `GameServer/Systems/RaidEncounter.cs` — 단일 액터 루프가 여러 세션의 피해를 순차 처리하는
   공유 보스 인코운터(기여도 누적·비례 보상·재등장·제한시간 실패까지 단위 테스트 완비, 예전 스레드
   샤딩 레이드 데모에서 이미 검증됨).
2. ServerLib의 `ISessionRegistry`/`BroadcastAsync` + `ServerNet.CreateSessionRegistry()` — 전체 세션에
   패킷을 브로드캐스트하는 경로(직전까지 `Main.cs`는 `CreateListener()`에 registry=null을 넘겨 미사용).

레이드 보스 마스터 데이터도 이미 존재했다: `MonsterTable.CreateDefault()`의 **7001 "레이드 보스"**
(`Hp=5_000_000, Atk=0, Def=50, ExpDrop=100_000, GoldDrop=200_000`) — 신규 데이터 불필요.

**범위 확정(사용자):** 세션별 독립 몬스터(`SessionBattleRunner`)를 공유 보스로 교체(그 클래스와
테스트는 git 이력에 보존, 이 서버 경로에서는 배선하지 않음). 제한시간(`RaidFailed`) 메커니즘 유지.
보스는 반격하지 않으므로(Atk=0) 플레이어는 죽지 않는다(순수 DPS 레이스).

## 2. 설계 결정

| 항목 | 채택 | 대안 | 사유 |
|------|------|------|------|
| 보스 HP 소유권 | `RaidEncounter` 액터 루프만 HP를 읽고 쓴다 | 세션 제출 루프가 직접 `monster.TakeDamage` | 사이클 1의 `BattleLoop`를 재사용하면 공유 보스 HP를 여러 스레드가 동시에 깎는 레이스가 됨 — `RaidEncounter`가 채널로 막으려는 바로 그것 |
| 브로드캐스트 실행 위치 | `RaidEncounter.RunAsync`의 선택적 `onStep` 콜백(액터 스레드에서 호출) | 별도 스레드가 보스 HP를 폴링해 브로드캐스트 | 보스 HP의 유일한 리더가 액터이므로, 다른 스레드가 읽으면 데이터 레이스. `onStep`은 사이클 1 `BattleLoop.onTick`과 동일한 비파괴적 확장 패턴 |
| `RaidEncounter` 다중 라이터 지원 | `_damageChannel`을 `SingleWriter=false`로 전환(코드 주석이 명시한 유일한 확장 지점) | 세션마다 별도 `RaidEncounter` 인스턴스 | 공유 보스는 정의상 인스턴스가 하나뿐이어야 함. 순수 판정 코어 시그니처는 무변경 — 기존 `RaidEncounterTests` 전부 무수정 통과 |
| 보상 크레딧 방식(가장 중요한 동시성 결정) | 드레인 루프(1개)가 `RewardReader`를 읽어 InstanceId로 세션의 `ConcurrentQueue`에 enqueue만 함. `Player.AddExp/AddGold`+레벨업은 그 Player를 소유한 **세션 제출 루프만** 수행 | 드레인 루프가 직접 `Player.AddExp` 호출 | 제출 루프가 동시에 `player.Update`/`UpdateFinalStats`를 재계산할 수 있어, 다른 스레드가 동시에 Player를 변경하면 레이스. 단일 소유 원칙 유지 |
| 접속 종료 후 도착한 보상 | 드롭(no-op) | Player 참조를 유지해 사후 크레딧 | 영속화가 없는 임시 Player라 무의미 — 무해한 드롭으로 문서화 |
| 세션 제출 루프 vs `BattleLoop.RunAsync` | 재사용하지 않음(신규 로직) | `BattleLoop` 재사용 | `BattleLoop.Tick`이 몬스터 HP를 직접 변경 — 공유 보스에 쓰면 레이스. 제출 루프는 보스의 불변 필드만 읽어 피해 숫자만 계산해 채널로 보냄 |
| `player.Update(deltaTime)` 호출 여부 | 호출하지 않음 | 매 틱 호출(사이클 1처럼) | 보스가 반격하지 않아 버프 틱·자연 회복이 결과에 영향 없음. FinalStats는 접속 시 장비 착용과 처치 시 레벨업 시점에만 갱신되면 충분 |
| 세션 CTS 취소 경로 | `new CancellationTokenSource()`(링크 없음), `OnDisconnected`에서만 `Cancel()`, `Dispose()` 생략 | `CreateLinkedTokenSource(수명토큰)` | 링크하면 부모(서버 수명) 토큰에 콜백이 등록되는데, 그 토큰은 서버 전체 생애 동안 살아있어 접속했다 끊은 모든 세션의 등록이 `Dispose()` 전까지 누적(누수). 링크를 아예 안 하면 애초에 `Register`가 없어 dispose 생략이 안전(사이클 1과 동일 전제, 이번엔 정확히 지킴) |
| HP 브로드캐스트 스로틀 | ~150ms 간격 제한(처치/실패는 항상 즉시) | 매 스텝마다 무조건 브로드캐스트 | N명 접속 시 틱당 N×N 전송 방지. 판정 자체(`RaidEncounter`)는 스로틀을 모른다 — 순수하게 네트워크 계층(`SessionRaidRunner`)의 책임 |
| 세대(Generation) 전달 방식 | `RaidStepBroadcast`가 `DeadGeneration`/`NewGeneration`을 명시적 필드로 둘 다 전달 | 네트워크 계층이 `Generation - 1`로 역산 | 액터가 이미 아는 값을 그대로 실어 보내 매퍼가 내부 증가 규칙을 추론할 필요를 없앰 |

### 알려진 한계(v1, 다음 사이클 개선 대상) — 2026-07-08 코드리뷰 후속 수정으로 해소됨

~~`onStep` 콜백(브로드캐스트 포함)은 액터의 `await foreach` 안에서 동기적으로 `await`된다 — 한
세션의 전송이 느려지면 그동안 액터의 다음 피해 소비가 지연되어 보스 처리가 **전원에 대해** 잠시
멈출 수 있다(`CheckDeadline`도 같이 밀림). 루프백 소켓의 소형 패킷 환경에서는 드러나지 않지만,
실사용 환경에서 느린 클라이언트가 전체를 막는 구조적 결합이다.~~

**해소됨:** 종합 코드 리뷰(`docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md`, HIGH
발견 — 성능·보안·아키텍처 3개 도메인이 독립적으로 같은 근본 원인을 지적)에서 이 결합이 단순한
지연을 넘어 무경계 `_damageChannel`의 무한 증가(OOM)로 이어질 수 있음이 확인되어, 예정보다
앞당겨 이번 사이클 내에 수정했다. `SessionRaidRunner`가 `onStep`(`BroadcastStepAsync`)을 이제
`_broadcastChannel`(신규, 액터→드레인 태스크 전용 채널)에 `TryWrite`만 하는 트리비얼 패스스루로
바꾸고, 실제 네트워크 전송·HP 스로틀 판정은 별도 `BroadcastDrainAsync` 드레인 태스크로 완전히
분리했다 — 액터는 브로드캐스트가 얼마나 느리거나 영원히 끝나지 않아도 전혀 영향받지 않는다.
같은 수정에서 스로틀 타임스탬프를 `await` 완료 이후에 찍도록 순서도 바로잡았고(이전에는 await
이전에 찍어 브로드캐스트가 150ms를 넘으면 스로틀이 무력화됐다), `Main.cs`에
`listener.SessionSendTimeout`(보안 Medium 발견의 완화책)도 함께 추가했다. 회귀 검증:
`SessionRaidRunnerBroadcastDecouplingTests.NeverRespondingBroadcast_DoesNotBlockActorDamageProcessing`
— 브로드캐스트가 절대 반환하지 않는 가짜 레지스트리를 주입해도 액터가 계속 전진함을 게이지 기록
횟수로 직접 검증한다.

### Medium 발견 후속 수정 — 2026-07-09

같은 코드 리뷰의 Medium 발견 6건 중 이번 사이클(공유 보스 co-op) 범위 안의 4건을 수정했다.
- **성능:** `SessionRaidRunner.SubmitLoopAsync`의 매 틱 `Task.Delay(interval, token)`을
  `PeriodicTimer` 재사용으로 교체(타이머 등록/해제 오버헤드 절감).
- **스타일:** `OnConnected`/`OnDisconnected`에 CLAUDE.md 인터페이스 문서화 규칙에 따른
  `<param>`/`<remarks>`(Thread Context/Blocking/Thread Safety) 추가, 방어적 분기(Player
  컨텍스트 없음/중복 SessionId/중복 해제) 회귀 테스트 3건 추가.
- **아키텍처(SRP 위반):** `SessionRaidRunner`가 안던 6가지 책임 중 브로드캐스트(직렬화/스로틀/
  전송)를 `RaidBroadcaster`로, 보상 라우팅+적용을 `RaidRewardApplier`로 분리했다(§3/§5 갱신).
  부수 효과로 `_byInstanceId` 중복 인덱스도 제거됨(`RaidRewardApplier`가 라우팅 등록을 전담).
  분리된 두 클래스는 `ISession` 전체를 흉내 내지 않고도 독립적으로 단위 테스트할 수 있어
  스로틀 경계·보상 드롭 분기 테스트도 함께 추가했다.

나머지 아키텍처 Medium 2건(`Systems/` 폴더를 Domain/Net으로 물리적으로 분리, `MobHpPacket`/
`MobDeathPacket`을 범용 `ServerLib`에서 `GameServer`로 이관)은 **의도적으로 보류**했다 — 사용자
확인 결과 전자는 이번 사이클의 확립된 평평한 `Systems/` 컨벤션을 바꾸는 결정이라 가치 대비 시급성이
낮고, 후자는 벤더 라이브러리(`ServerLib`)를 수정하고 사이클 1의 `SessionBattlePackets`와 그 테스트까지
함께 건드려야 하는 사이클 밖 범위라 판단했다. 두 항목 모두
`docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md`에 미해결로 남아 있다.

## 3. 컴포넌트 구조

```
GameServer/
├─ Main.cs                          (수정 — SessionBattleRunner 배선 제거, SessionRaidRunner로 교체)
├─ Systems/
│  ├─ RaidEncounter.cs              (수정 — SingleWriter=false, onStep 콜백, RaidStepBroadcast 신설,
│  │                                  private _generation/_lastMvpName/_lastTopDamage — 순수 코어 시그니처는 무변경)
│  ├─ RaidBroadcastPackets.cs       (신규 — RaidStepBroadcast → MobHpPacket/MobDeathPacket 순수 매퍼)
│  ├─ RaidBroadcaster.cs            (신규, 2026-07-09 SRP 분리 — onStep 수신 + 채널 드레인 + 직렬화/스로틀/전송)
│  ├─ RaidRewardApplier.cs          (신규, 2026-07-09 SRP 분리 — 보상 라우팅(드레인 루프) + ApplyPending 정적 헬퍼)
│  ├─ SessionRaidRunner.cs          (신규 — SessionPlayerBinder와 나란히 ISession을 다루는 네트워크 계층,
│  │                                  2026-07-09부터 세션 생명주기·제출 루프 오케스트레이션만 담당)
│  └─ SessionBattleRunner.cs        (수정 없음 — git 이력에 보존, 이 서버 경로에서는 배선하지 않음)
└─ (Entities/Items/Combat/Stats/기타 Systems — 변경 없음)

tests/GameServer.Tests/Systems/
├─ RaidEncounterTests.cs                        (무수정 — 순수 코어 회귀 검증, 전부 통과)
├─ RaidEncounterConcurrencyTests.cs              (신규 — 다중 라이터 동시 SubmitDamage 검증)
├─ RaidEncounterBroadcastTests.cs                (신규 — onStep MVP/세대 전환 시임 테스트)
├─ RaidBroadcastPacketsTests.cs                  (신규 — 순수 매퍼 단위 테스트)
├─ SessionRaidRunnerEndToEndTests.cs             (신규 — 실 루프백 소켓 2연결 co-op 통합 테스트)
├─ SessionRaidRunnerBroadcastDecouplingTests.cs  (신규, 2026-07-08 HIGH 수정 — 액터-전송 분리 회귀 검증)
├─ RaidBroadcasterTests.cs                       (신규, 2026-07-09 SRP 분리 — HP 스로틀 경계 검증)
├─ RaidRewardApplierTests.cs                     (신규, 2026-07-09 SRP 분리 — 라우팅/드롭/적용 검증)
└─ SessionRaidRunnerEdgeCaseTests.cs             (신규, 2026-07-09 — 방어적 분기 회귀 검증)
```

의존 관계:
```
RaidEncounter (도메인) — ServerLib 비참조, onStep은 도메인 struct(RaidStepBroadcast)만 사용
   ↑
RaidBroadcastPackets (도메인, 소켓 없는 순수 매퍼) — ServerLib 패킷 타입만 참조
   ↑
RaidBroadcaster / RaidRewardApplier (네트워크 계층, 2026-07-09 분리) — 각각 ISessionRegistry
로의 전송, PlayerInstanceId 라우팅만 안다
   ↑
SessionRaidRunner (네트워크 계층) — ISession을 다루는 유일한 지점(SessionPlayerBinder와 나란히),
위 두 클래스를 조립해 세션 생명주기만 오케스트레이션
   ↑
Main.cs — registry 생성 → CreateListener(registry) → raidRunner.Start(수명토큰)
```

## 4. 핵심 API

```csharp
// RaidEncounter: onStep 콜백을 주입하면 매 스텝(피해 적용/데드라인 검사)마다 브로드캐스트 정보를 받는다.
await raid.RunAsync(sink, lifetimeToken, onStep: async (info, ct) =>
{
    var (death, hp) = RaidBroadcastPackets.Build(info);
    if (death is not null) await BroadcastPacketAsync(death, ct);
    await BroadcastPacketAsync(hp, ct);
});

// SessionRaidRunner: IServerListener 콜백에 binder와 나란히 배선(연결 순서: binder → raidRunner)
var registry = ServerNet.CreateSessionRegistry();
var raidRunner = new SessionRaidRunner(levelSystem, monsterTable, equipmentTable, sink, registry,
    raidTimeLimit: TimeSpan.FromSeconds(60));
raidRunner.Start(cts.Token); // 액터 루프 + 보상 드레인 루프 기동

IServerListener listener = ServerNet.CreateListener(registry);
listener.OnClientConnected = async session =>
{
    await binder.OnConnected(session);
    await raidRunner.OnConnected(session); // 장비 착용 + 세션 제출 루프 시작
};
listener.OnClientDisconnected = async session =>
{
    await raidRunner.OnDisconnected(session); // 제출 루프 취소 + 딕셔너리 제거
    await binder.OnDisconnected(session);
};
```

## 5. 변경 파일 목록

| 파일 | 구분 | 내용 |
|------|------|------|
| `GameServer/Systems/RaidEncounter.cs` | 수정 | `_damageChannel` SingleWriter=false, `RunAsync`에 선택적 `onStep` 추가, `RaidStepBroadcast` 신설, private 세대/MVP 상태 추가(순수 코어 시그니처 무변경) |
| `GameServer/Systems/RaidBroadcastPackets.cs` | 신규 | `RaidStepBroadcast` → `(MobDeathPacket?, MobHpPacket)` 순수 매퍼 |
| `GameServer/Systems/SessionRaidRunner.cs` | 신규 | 공유 보스 co-op 네트워크 배선(제출 루프·드레인 루프·브로드캐스트) |
| `GameServer/Main.cs` | 수정 | `SessionBattleRunner` 배선 제거, 세션 레지스트리 생성 + `SessionRaidRunner`로 교체, `listener.SessionSendTimeout` 설정 |
| `GameServer/Systems/RaidBroadcaster.cs` | 신규(2026-07-09) | SRP 분리 — onStep 수신 + 채널 드레인 + 직렬화/스로틀/전송 |
| `GameServer/Systems/RaidRewardApplier.cs` | 신규(2026-07-09) | SRP 분리 — 보상 라우팅(드레인 루프) + `ApplyPending` 정적 헬퍼 |
| `tests/GameServer.Tests/Systems/RaidEncounterConcurrencyTests.cs` | 신규 | 다중 라이터 동시 제출 검증(유실/중복 없음) |
| `tests/GameServer.Tests/Systems/RaidEncounterBroadcastTests.cs` | 신규 | onStep MVP/TopDamage/세대 전환 검증 |
| `tests/GameServer.Tests/Systems/RaidBroadcastPacketsTests.cs` | 신규 | 순수 매퍼 단위 테스트 5건 |
| `tests/GameServer.Tests/Systems/SessionRaidRunnerEndToEndTests.cs` | 신규 | 실 루프백 소켓 2연결 co-op 통합 테스트 |
| `tests/GameServer.Tests/Systems/SessionRaidRunnerBroadcastDecouplingTests.cs` | 신규(2026-07-08) | HIGH 발견 회귀 — 브로드캐스트가 액터를 막지 않음을 검증 |
| `tests/GameServer.Tests/Systems/RaidBroadcasterTests.cs` | 신규(2026-07-09) | HP 스로틀 경계, BossDefeated는 스로틀 무관 즉시 전송 |
| `tests/GameServer.Tests/Systems/RaidRewardApplierTests.cs` | 신규(2026-07-09) | 라우팅/미등록 드롭/Unregister 이후 차단/ApplyPending |
| `tests/GameServer.Tests/Systems/SessionRaidRunnerEdgeCaseTests.cs` | 신규(2026-07-09) | Player 컨텍스트 없음/중복 SessionId/중복 해제 방어적 분기 |

## 6. 빌드 검증

```powershell
dotnet build IDLE_RPG.sln
dotnet test tests/GameServer.Tests/GameServer.Tests.csproj
dotnet test tests/EchoExample.Tests/EchoExample.Tests.csproj
```

**검증 결과(2026-07-08):** 빌드 0 에러/0 경고(ServerLib의 기존 CS0419 경고 10건은 이번 변경과
무관, 이전 사이클부터 존재). `GameServer.Tests` 144/144 통과(신규 동시성 테스트 5회 연속 재실행,
신규 통합 테스트 6회 연속 재실행 모두 플레이키 없음 확인) / `EchoExample.Tests` 13/13 통과(회귀 없음).

**실제 런타임 스모크 테스트(공유 보스 실증):** `dotnet run --project GameServer`로 서버를 7777에
기동한 뒤, **두 개의 독립 raw TCP 소켓**을 동시에 접속해 각각 8초간 수신 바이트를 캡처했다.
`diff`로 비교한 결과 **두 캡처 파일이 완전히 바이트 단위로 동일**했다 — 이는 두 클라이언트가
정확히 같은 공유 보스의 `MobHpPacket` 시퀀스(PacketId=6, MaxHp=5,000,000 그대로, HP가 두 플레이어의
기여로 함께 감소)를 받았음을 실제 네트워크 위에서 실증한다. `logs/game-events.ndjson`에도 서로 다른
`playerId` 2개의 `PlayerConnected` → (8초 타임아웃으로 연결 종료 시) `PlayerDisconnected` 2건이
정상 기록됨을 확인. 프로세스 종료 후 잔여 프로세스 없음, 포트는 `TIME_WAIT`만 남음 확인.

(자동화된 `SessionRaidRunnerEndToEndTests`가 소형 커스텀 보스로 처치·`MobDeathPacket`·MVP 일치까지
결정론적으로 검증하므로, 수동 스모크는 기본 보스 설정으로 "공유 상태 실시간 동기화" 자체의 실증에
집중했다 — 기본 보스 HP=5,000,000은 8초 안에 죽지 않는 것이 의도된 정상 동작이다.)

## 7. 향후 확장 포인트

- ~~브로드캐스트를 액터 루프에서 분리~~ — **완료(2026-07-08 코드리뷰 후속 수정, §2 참고).**
- ~~SessionRaidRunner의 SRP 위반 해소~~ — **완료(2026-07-09 코드리뷰 후속 수정, §2 참고).**
- (보류) `Systems/` 폴더를 Domain/Net으로 물리적으로 분리 — 평평한 컨벤션을 바꾸는 결정이라 사용자 확인 후 착수.
- (보류) `MobHpPacket`/`MobDeathPacket`을 `ServerLib`에서 `GameServer.Net.Packets`로 이관 — 사이클 1의
  `SessionBattlePackets`/테스트까지 함께 건드려야 하는 벤더 라이브러리 수정, 별도 사이클로 분리 권장.
- 브로드캐스트 스로틀 정교화(HP diff 임계·적응형 주기), 신규 접속자에게 현재 보스 HP 스냅샷 1회 즉시 푸시.
- 보스 페이즈/버프(현재 불변식상 `boss.Update` 금지 — 페이즈 도입 시 액터 단일 스레드에서만 재계산하도록 재설계 필요).
- 아이템 드롭 분배(현재 레이드는 Exp/Gold만 비례 분배, `AcquiredItems`는 미분배).
- PvP, 실제 로그인(현재도 `AccountId=0` 임시값), 스테이지/스포너(고정 보스 7001·고정 장비 대체).
