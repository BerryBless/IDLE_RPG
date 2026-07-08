# 전투 멀티플레이 1단계: 세션별 독립 몬스터 자동 전투 네트워크 푸시

작성일: 2026-07-08

## 1. 배경 및 목적

지난 사이클(`plan/client_server_split_0708.md`)에서 GameServer는 실제 TCP 서버(포트 7777)가
되었지만, 소켓이 연결될 때 임시 `Player`를 생성해 붙이는 것까지만 했다 — 전투는 아직 아무것도
일어나지 않는다. `GameServer/Systems/BattleLoop.cs`(단일 Player vs 단일 Monster 라운드제 전투)와
`BattleManager`(스레드 안전한 데미지 계산 싱글턴)는 예전 400명 스레드 샤딩 콘솔 데모에서 이미
검증된 채로 남아있지만, 지금은 아무도 호출하지 않는다.

이번 사이클의 목적은 "전투를 멀티로" 만드는 것이다. 사용자가 확정한 범위: **접속한 각
클라이언트가 자신만의 독립적인 몬스터를 동시에 사냥한다**(공유 보스 co-op는 사용자가 원하는
차차기 사이클로 분리 확정). 이 게임은 방치형(idle) 장르이므로, **전투는 서버가 자동으로 틱한다**
— 클라이언트는 아무 명령도 보내지 않고 결과(몬스터 HP·처치)만 소켓으로 수신한다. 따라서 이번
사이클엔 `listener.OnReceived` 배선이 전혀 필요 없다(순수 서버→클라이언트 푸시).

ServerLib에는 이미 이 시나리오를 위해 설계된 것으로 보이는 패킷이 존재했다 — `MobHpPacket`(Id=6,
서버→클라이언트 주기적 HP 브로드캐스트용, `Generation` 필드로 몬스터 재등장 구분)과
`MobDeathPacket`(Id=7, 처치 시 1회 전송, MVP 개념 포함 — 이번 사이클은 1인 전투라 MVP는 자기
자신으로 고정). 둘 다 지금까지 아무 데서도 쓰이지 않던 것을 그대로 재사용했다.

## 2. 설계 결정

| 항목 | 채택 | 대안 | 사유 |
|------|------|------|------|
| 세션별 전투 상태 저장 위치 | 신규 클래스 전용 `ConcurrentDictionary<Guid, SessionBattleContext>`(SessionId 키) | `SessionPlayerBinder`가 이미 쓰는 `session.Context`를 확장 | `session.Context`는 단일 슬롯이라 이미 `Player`가 점유 중. 확장하면 이미 병합·테스트된 `SessionPlayerBinder`/`SessionConnectionEndToEndTests`가 깨짐 |
| 신규 클래스 | `SessionBattleRunner`(`SessionPlayerBinder`와 나란히 `ISession`을 다루는 두 번째이자 마지막 클래스) | `Main.cs`에 인라인 | `OnConnected`/`OnDisconnected`로 동일한 모양을 가져 `Main.cs`에서 조합하기 쉽고, 소켓 통합 테스트로 독립 검증 가능 |
| 전투 루프 시작 방식 | `_ = Task.Run(...)` fire-and-forget | `OnConnected` 안에서 직접 `await` | `SocketPipelineListener.AcceptLoopAsync`가 accept 1건마다 `OnClientConnected`를 단일 accept 루프 안에서 직접 `await`함(소스 확인) — 무한 전투 루프를 여기서 기다리면 그 순간부터 새 연결을 전혀 받지 못한다 |
| `BattleLoop.RunAsync` 확장 | 6번째 선택 인자 `onTick` 콜백 추가(비파괴적) | 새 클래스가 `BattleLoop.Tick`을 직접 반복 호출 | `LogTick`이 `private static`이라 직접 반복하면 기존 관측성(`sink.RecordMonsterDefeated` 등)을 잃고 이미 테스트된 루프 로직을 중복시킴. 기존 호출부는 위치 인자 바인딩이라 그대로 컴파일됨 |
| 패킷 전송 범위 | 그 세션에게만(`session.SendAsync<T>`) | `ISessionRegistry.BroadcastAsync` | 이번 사이클은 세션별 독립 몬스터라 브로드캐스트가 필요 없음(공유 보스 co-op 사이클로 미룸) |
| 시작 몬스터/장비 | 고정값(몬스터 2003 고블린, 무기 4001/방어구 5001/장신구 6001) | 스테이지/스포너 시스템 설계 | 아직 스테이지 시스템이 없음 — 예전 400명 샤딩 데모가 쓰던 값을 그대로 재사용(YAGNI, 차차기 과제로 명시) |
| 1인 전투 MVP 관례 | `MvpName = player.InstanceId`, `TopDamage = monster.FinalStats.MaxHp` 전량 | 데미지 누적 트래킹 도입 | 유일한 기여자이므로 전체 HP풀 = 100% 기여로 취급해도 무손실. 공유 보스 사이클에서 실제 누적 데미지 기반으로 교체 예정임을 문서화 |
| CTS 정리 | 루프 `finally`에서 `TryRemove`만, `Dispose()` 안 함 | `Dispose()`도 같이 호출 | CTS는 `OnDisconnected`(소켓 종료)와 `OnTickAsync`의 송신 실패 자가취소, 두 경로에서 취소될 수 있다. `Dispose()`한 뒤 다른 경로가 뒤늦게 `Cancel()`하면 `ObjectDisposedException`이 나 disconnect 처리(binder의 연결 해제 기록)까지 깨진다. `RunAsync`가 이 토큰으로 `WaitHandle`을 할당하지 않으므로 dispose 생략은 누수 없음(GC 회수) |
| 송신 실패 처리 | 그 세션의 CTS만 취소, 예외는 삼킴 | 예외를 상위로 전파 | 한 연결의 송신 실패가 다른 연결에 영향을 주지 않아야 함(`ShardBattleRunner`/`RaidEncounter`와 동일한 격리 원칙) |

### 검증된 전제 (소스로 직접 확인)

- `EquipmentTable(IReadOnlyList<EquipmentTemplate> templates)` 공개 생성자가 이미 존재
  (`GameServer/Items/EquipmentTable.cs:29`, 2026-07-06 코드리뷰에서 테스트 주입 목적으로 추가됨)
  — 커스텀 무기 스탯 테이블로 결정론적 통합 테스트 설계가 가능함을 확인.
- `MonsterTable.CreateDefault()`의 고블린(2003)은 `Hp=55, Atk=8, Def=2`
  (`GameServer/Systems/MonsterTable.cs:53`) — 100만 Atk 무기면 확실히 1틱 즉사, `BattleLoop.Tick`은
  몬스터가 죽으면 `return BattleTickEvent.MonsterDefeated`로 반격 분기 이전에 종료하므로
  (`GameServer/Systems/BattleLoop.cs:88-96`) 플레이어는 절대 데미지를 받지 않는다 — 통합 테스트가
  타이밍 경합 없이 결정론적으로 동작함을 확인.
- `SocketPipelineListener.AcceptLoopAsync`가 accept 1건마다 `OnClientConnected`를 그 루프 안에서
  직접 `await`한다 — fire-and-forget이 필수임을 재확인.

## 3. 컴포넌트 구조

```
GameServer/
├─ Main.cs                          (수정 — MonsterTable/EquipmentTable 재구성, SessionBattleRunner 배선)
├─ Systems/
│  ├─ BattleLoop.cs                 (수정 — RunAsync에 선택적 onTick 콜백 6번째 인자 추가)
│  ├─ SessionBattlePackets.cs       (신규 — 소켓 없는 순수 매퍼: 틱 결과 → MobHpPacket/MobDeathPacket)
│  └─ SessionBattleRunner.cs        (신규 — SessionPlayerBinder와 나란히 ISession을 다루는 두 번째 클래스)
└─ (Entities/Items/Combat/Stats/기타 Systems — 변경 없음, 기존 도메인 클래스·테스트 그대로 유지)

tests/GameServer.Tests/Systems/
├─ BattleLoopTests.cs                          (수정 — onTick 콜백 호출 검증 케이스 1건 추가)
├─ SessionBattlePacketsTests.cs                 (신규 — 순수 단위 테스트 4건)
└─ SessionBattleRunnerEndToEndTests.cs          (신규 — 실 루프백 소켓 통합 테스트 2건)
```

의존 관계:
```
GameServer.Systems.BattleLoop / SessionBattlePackets
   — ServerLib를 전혀 참조하지 않음(onTick 콜백 시그니처는 Player/Monster/BattleTickEvent만 사용)
   — 도메인/네트워크 경계 유지(PlayerFactory.CreateTemp와 동일한 원칙)

SessionBattleRunner (신규, SessionPlayerBinder와 나란히)
   — 유일하게 ISession을 다루는 두 번째 GameServer 타입
   — BattleLoop.RunAsync(..., onTick: 소켓 전송)을 Task.Run(fire-and-forget)으로 세션마다 시작
```

## 4. 핵심 API

```csharp
// BattleLoop: onTick 콜백을 주입하면 매 틱마다 결과를 전달받는다 (기존 호출부는 무수정으로 컴파일)
await loop.RunAsync(player, monster, tickInterval, cancellationToken, sink,
    onTick: (evt, p, m, ct) => sessionBattleRunner.OnTickAsync(session, ctx, evt, p, m, ct));

// SessionBattlePackets: 순수 매퍼, 세대(Generation) 규칙 포함
var set = SessionBattlePackets.BuildTickPackets(evt, player, monster, currentGeneration);
if (set.Death is not null) await session.SendAsync(set.Death, ct);
await session.SendAsync(set.Hp, ct);

// SessionBattleRunner: IServerListener 콜백에 binder와 나란히 배선 (연결 순서: binder → battleRunner)
var battleRunner = new SessionBattleRunner(levelSystem, monsterTable, equipmentTable, sink);
listener.OnClientConnected = async session =>
{
    await binder.OnConnected(session);
    await battleRunner.OnConnected(session);
};
listener.OnClientDisconnected = async session =>
{
    await battleRunner.OnDisconnected(session); // 전투 루프 먼저 정지
    await binder.OnDisconnected(session);
};
```

## 5. 변경 파일 목록

| 파일 | 구분 | 내용 |
|------|------|------|
| `GameServer/Systems/BattleLoop.cs` | 수정 | `RunAsync`에 6번째 선택 인자 `onTick` 추가, `LogTick` 직후 호출 |
| `GameServer/Systems/SessionBattlePackets.cs` | 신규 | 틱 결과 → `MobHpPacket`/`MobDeathPacket` 순수 매퍼(`internal static`) |
| `GameServer/Systems/SessionBattleRunner.cs` | 신규 | 세션별 `ConcurrentDictionary` 기반 전투 컨텍스트 관리, 연결 시 자동 전투 시작·해제 시 취소 |
| `GameServer/Main.cs` | 수정 | `MonsterTable`/`EquipmentTable` 재구성, `SessionBattleRunner` 생성·배선 |
| `tests/GameServer.Tests/Systems/BattleLoopTests.cs` | 수정 | `RunAsync_InvokesOnTickPerTick_UntilCancelled` 추가 |
| `tests/GameServer.Tests/Systems/SessionBattlePacketsTests.cs` | 신규 | None/PlayerDefeated/MonsterDefeated/HP 클램프 4건 |
| `tests/GameServer.Tests/Systems/SessionBattleRunnerEndToEndTests.cs` | 신규 | 실 루프백 소켓 2건: 단일 연결 HP→Death 수신, 동시 2연결 세션별 MVP 격리 |

## 6. 빌드 검증

```powershell
dotnet build IDLE_RPG.sln
dotnet test tests/GameServer.Tests/GameServer.Tests.csproj
```

**검증 결과(2026-07-08):** 빌드 0 에러/0 경고. `GameServer.Tests` 134/134 통과(신규 통합 테스트
`Connect_ReceivesMobHpPacket_ThenMobDeathPacket` 6회 연속 재실행, `TwoConcurrentConnections_
EachReceivesOwnDistinctMvpName` 5회 연속 재실행 모두 플레이키 없음 확인) / `EchoExample.Tests`
13/13 통과(회귀 없음).

**동시 접속 격리 검증:** 처음엔 클라이언트 1개짜리 테스트/스모크만 있었는데, "멀티"의 핵심 주장인
"세션별 독립 몬스터"는 N=1로는 증명되지 않는다는 지적(코드 리뷰 시 advisor 피드백)에 따라
`TwoConcurrentConnections_EachReceivesOwnDistinctMvpName`을 추가했다 — 소켓 2개를 동시에 연결해
각자 다른 `MobDeathPacket.MvpName`(자기 자신의 `player-{guid}`)을 받는지 확인, `SessionBattleRunner`
내부 `_battles` 딕셔너리의 세션별 격리가 교차 오염 없이 동작함을 실증했다.

**실제 런타임 스모크 테스트:** `dotnet run --project GameServer`로 서버를 7777에 기동한 뒤 raw TCP
소켓으로 접속해 wire 바이트를 직접 캡처·헥스 디코드했다. 확인된 시퀀스: `MobHpPacket`(id=6)이
Generation 1에서 HP 38→22→6으로 감소 → `MobDeathPacket`(id=7, `TopDamage=55`,
`MvpName="player-054d96e34933400380ad92fcfcb88db2"`) → 재등장한 `MobHpPacket`이 Generation 2에서
`Hp=55/MaxHp=55`로 리셋. `logs/game-events.ndjson`도 동일 playerId로 `PlayerConnected` → 3×
`MonsterDefeated`(exp/gold 6/8→12/16→18/24로 정상 누적) → `PlayerDisconnected` 순서를 기록했다.
프로세스 종료 후 잔여 프로세스·포트 점유(TIME_WAIT 제외) 없음도 확인.

## 7. 향후 확장 포인트

- **공유 보스 co-op:** 사용자가 이미 원한다고 확정한 차차기 목표. `RaidEncounter`(이미 존재,
  `SingleWriter=true`라 다중 세션 프로듀서를 받으려면 `SingleWriter=false`로 전환 필요)를 네트워크
  세션 다중 프로듀서로 적응. 이때 `ISessionRegistry.BroadcastAsync`(raw bytes)로 모든 접속자에게
  HP/처치를 브로드캐스트.
- PvP.
- 실제 로그인(현재도 `AccountId=0` 임시값 유지).
- 스테이지/스포너 시스템 도입 시 고정 몬스터(2003)/고정 장비(4001/5001/6001) 하드코딩을 대체.
