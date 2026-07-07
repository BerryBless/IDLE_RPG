# 다중 플레이어 배틀 스레드 샤딩 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `GameServer/Main.cs`를 확장해, 스레드당 100명씩 나뉜 다수의 Player/Monster 쌍이 전용 `Thread`에서 동시에 독립적으로 전투를 진행하는 데모로 교체한다.

**Architecture:** `ThreadCount`(상수) × `PlayersPerThread`(100 고정)명의 Player/Monster 쌍을 생성해 `ThreadCount`개의 샤드로 나눈다. 샤드마다 전용 `Thread`를 하나씩 띄워, 그 안에서 자기 몫의 100명을 동기 `foreach`로 순회하며 `BattleLoop.Tick`을 호출한다(`ShardBattleRunner.TryTick`으로 감싸 쌍 단위 예외 격리). 처치/사망 이벤트만 `BattleEventLogger.Format`으로 콘솔에 출력한다. `BattleManager`의 내부 `Random`을 `Random.Shared`로 바꿔 여러 샤드 스레드의 동시 호출을 안전하게 만든다.

**Tech Stack:** .NET 10 (`net10.0`), C# top-level statements(`GameServer/Main.cs`), xUnit(`GameServer.Tests`).

## Global Constraints

- `PlayersPerThread` = 100 (고정, 설계 문서 결정 — 조정 대상 아님)
- `ThreadCount`는 `Main.cs` 상수로 노출해 실행 전 조정 가능해야 함
- 로깅은 `MonsterDefeated`/`PlayerDefeated` 이벤트만 출력하고, 매 틱 HP 상태 로그는 출력하지 않음. 모든 로그 줄은 `[player-XXXX]` 형태로 플레이어 인스턴스 ID를 프리픽스로 붙임
- `BattleManager`의 기본 생성자 경로는 `Random.Shared`를 사용해야 함(스레드 안전). 테스트용 결정적 시드 주입 생성자(`internal BattleManager(Random random)`)는 그대로 유지
- 샤드 루프는 Player/Monster 쌍 단위로 `try/catch`를 감싸 예외를 격리해야 함 — 전용 `Thread`의 미처리 예외는 프로세스 전체를 종료시키므로, 예외 발생 시 로그만 남기고 해당 쌍을 건너뜀(자동 복구·재시작 로직은 범위 밖)
- `GameServer/Systems/BattleLoop.cs`의 `Tick`/`RunAsync`/`LogTick`은 변경하지 않음
- 파티 co-op, PvP, 런타임 플레이어 추가/제거(동적 매니저)는 이번 계획의 범위 밖
- 타깃 프레임워크 `net10.0`, `GameServer.csproj`는 이미 `GameServer.Tests`에 `InternalsVisibleTo`를 부여함(추가 설정 불필요)
- CLAUDE.md 문서화 규칙: 신규 `public` 타입/메서드는 XML `<remarks>`에 Thread Safety/Memory Allocation/Blocking 여부를 명시해야 함. 네트워크·동시성 관련 타입(`Thread` 등)을 선언할 때는 "왜 이 타입을 선택했는지"를 내부 동작 근거로 인라인 주석에 남겨야 함

---

### Task 1: BattleManager를 Random.Shared로 전환(스레드 안전 확보)

**Files:**
- Modify: `GameServer/Systems/BattleManager.cs:9-43` (클래스 `<remarks>` + `private BattleManager()` 생성자)
- Test: `tests/GameServer.Tests/Systems/BattleManagerTests.cs`

**Interfaces:**
- Consumes: 없음(기존 `BattleManager.Instance` 싱글턴, `internal BattleManager(Random random)` 시드 주입 생성자 — 둘 다 이미 존재)
- Produces: `BattleManager.Instance.CalcFinalDamage(...)`가 여러 스레드에서 동시에 호출돼도 안전해짐(공개 시그니처 변경 없음)

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/GameServer.Tests/Systems/BattleManagerTests.cs` 파일 맨 위 `using` 목록에 `using System.Reflection;`을 추가하고, 클래스 맨 아래(마지막 `}` 앞)에 다음 테스트를 추가한다:

```csharp
    [Fact]
    public void Instance_DefaultConstructorPath_UsesRandomSharedForThreadSafety()
    {
        // 다중 플레이어 배틀 스레드 샤딩 사이클: BattleManager.Instance가 여러 샤드 스레드에서
        // 동시 호출되므로, 기본 생성자 경로가 스레드 안전한 Random.Shared를 쓰는지 반사로
        // 검증한다. Random.Shared는 프로세스 전역에서 항상 같은 인스턴스를 반환하므로
        // 참조 동일성 비교가 결정적이다.
        var field = typeof(BattleManager).GetField("_random", BindingFlags.NonPublic | BindingFlags.Instance);

        var randomFieldValue = field!.GetValue(BattleManager.Instance);

        Assert.Same(Random.Shared, randomFieldValue);
    }
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter Instance_DefaultConstructorPath_UsesRandomSharedForThreadSafety`
Expected: FAIL — `Assert.Same()` 실패(`_random`이 `new Random()`이라 `Random.Shared`와 참조가 다름)

- [ ] **Step 3: BattleManager.cs 수정**

`GameServer/Systems/BattleManager.cs`에서 클래스 `<remarks>`의 Thread Safety 항목(파일 12~14번째 줄 근방)을 다음으로 교체:

```csharp
/// <item><description><b>Thread Safety:</b> <see cref="Instance"/>(기본 생성자 경로)는 Thread-safe.
/// 내부 <see cref="_random"/>이 <see cref="System.Random.Shared"/>(.NET 6+, 스레드 안전)를 사용하므로
/// 여러 샤드 스레드가 동시에 <see cref="CalcFinalDamage"/>를 호출해도 안전하다(다중 플레이어 배틀
/// 스레드 샤딩 사이클에서 수정). 단, <see cref="BattleManager(Random)"/>로 별도 <see cref="Random"/>을
/// 주입하면(주로 테스트의 결정적 시드 목적) 그 인스턴스는 더 이상 스레드 안전을 보장하지 않으므로
/// 단일 스레드에서만 사용해야 한다.</description></item>
```

그리고 기본 생성자를:

```csharp
    private BattleManager() : this(new Random())
    {
    }
```

다음으로 교체:

```csharp
    private BattleManager() : this(Random.Shared)
    {
    }
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter Instance_DefaultConstructorPath_UsesRandomSharedForThreadSafety`
Expected: PASS

- [ ] **Step 5: 전체 테스트 회귀 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj`
Expected: 기존 테스트 전부 PASS(신규 1건 포함). `BattleManagerTests`의 다른 테스트(예: `CalcFinalDamage_WeaponEquipped_AutomaticallyAppliesWeaponAttackScaling`)는 `internal BattleManager(Random random)` 경로를 쓰므로 영향 없어야 한다.

- [ ] **Step 6: 커밋**

```bash
git add GameServer/Systems/BattleManager.cs tests/GameServer.Tests/Systems/BattleManagerTests.cs
git commit -m "$(cat <<'EOF'
수정: BattleManager 기본 경로를 Random.Shared로 전환

다중 플레이어 배틀에서 여러 샤드 스레드가 BattleManager.Instance를
동시에 호출하므로, 스레드 안전하지 않던 내부 Random을 Random.Shared로
교체 - 시드 주입 테스트 경로는 그대로 유지.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: BattleEventLogger 추가 (이벤트 전용 로그 포맷터)

**Files:**
- Create: `GameServer/Systems/BattleEventLogger.cs`
- Test: Create `tests/GameServer.Tests/Systems/BattleEventLoggerTests.cs`

**Interfaces:**
- Consumes: `GameServer.Systems.BattleTickEvent`(기존 enum, `GameServer/Systems/BattleLoop.cs`), `GameServer.Entities.Player`(기존 — `InstanceId`/`Level`/`CurrentExp`/`CurrentGold` 프로퍼티 사용)
- Produces: `GameServer.Systems.BattleEventLogger.Format(string instanceId, BattleTickEvent result, Player player) : string` — Task 4(Main.cs)에서 사용

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/GameServer.Tests/Systems/BattleEventLoggerTests.cs` 생성:

```csharp
using GameServer.Entities;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class BattleEventLoggerTests
{
    private static Player MakePlayer(int level, BigNumber exp, BigNumber gold) =>
        new() { InstanceId = "unused", AccountId = 1, Level = level, CurrentExp = exp, CurrentGold = gold };

    [Fact]
    public void Format_MonsterDefeated_IncludesInstanceIdLevelExpAndGold()
    {
        var player = MakePlayer(level: 3, exp: 41, gold: 22);

        var log = BattleEventLogger.Format("player-0042", BattleTickEvent.MonsterDefeated, player);

        Assert.Equal("[player-0042] [처치] 몬스터 처치! Lv.3 누적 Exp=41, Gold=22", log);
    }

    [Fact]
    public void Format_PlayerDefeated_IncludesInstanceIdOnly()
    {
        var player = MakePlayer(level: 1, exp: 0, gold: 0);

        var log = BattleEventLogger.Format("player-0187", BattleTickEvent.PlayerDefeated, player);

        Assert.Equal("[player-0187] [부활] 플레이어 사망 → 즉시 부활", log);
    }

    [Fact]
    public void Format_NoneEvent_ReturnsEmptyString()
    {
        var player = MakePlayer(level: 1, exp: 0, gold: 0);

        var log = BattleEventLogger.Format("player-0001", BattleTickEvent.None, player);

        Assert.Equal(string.Empty, log);
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter BattleEventLoggerTests`
Expected: FAIL — 컴파일 오류(`BattleEventLogger` 타입이 존재하지 않음)

- [ ] **Step 3: 최소 구현 작성**

`GameServer/Systems/BattleEventLogger.cs` 생성:

```csharp
using GameServer.Entities;

namespace GameServer.Systems;

/// <summary>
/// <see cref="BattleTickEvent"/>를 사람이 읽을 콘솔 로그 문자열로 변환한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 정적 필드나 공유 가변 상태가 없고,
/// 인자로 받은 값만으로 새 문자열을 계산해 반환하는 순수 함수다. 여러 샤드 스레드가 동시에
/// 호출해도 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 호출마다 보간 문자열 1개를 새로 할당한다
/// (<see cref="BattleTickEvent.None"/>이면 <see cref="string.Empty"/>를 반환해 할당 없음).</description></item>
/// <item><description><b>Blocking 여부:</b> 즉시 반환(동기, non-blocking). I/O 없음(콘솔 출력은
/// 호출 측 책임).</description></item>
/// </list>
/// </remarks>
public static class BattleEventLogger
{
    /// <summary>
    /// 이번 틱의 사망 이벤트를 <paramref name="instanceId"/>가 식별하는 플레이어 기준으로 포맷한다.
    /// </summary>
    /// <param name="instanceId">이벤트가 발생한 플레이어의 <see cref="Entity.InstanceId"/></param>
    /// <param name="result">포맷할 사망 이벤트</param>
    /// <param name="player">누적 경험치·골드·레벨을 읽어올 플레이어(주로 <paramref name="instanceId"/> 소유자)</param>
    /// <returns>
    /// <see cref="BattleTickEvent.MonsterDefeated"/>/<see cref="BattleTickEvent.PlayerDefeated"/>는
    /// 프리픽스가 붙은 한 줄 로그 문자열, <see cref="BattleTickEvent.None"/>은 <see cref="string.Empty"/>.
    /// </returns>
    /// <remarks>
    /// 다중 플레이어 샤드 루프에서 매 틱 HP 상태까지 출력하면 콘솔이 넘치므로(스레드당 100명 규모),
    /// 호출 측(<c>Main.cs</c>)이 <see cref="BattleTickEvent.None"/>일 때 이 결과를 그대로 출력하지
    /// 않도록 빈 문자열로 신호를 준다.
    /// </remarks>
    public static string Format(string instanceId, BattleTickEvent result, Player player) => result switch
    {
        BattleTickEvent.MonsterDefeated =>
            $"[{instanceId}] [처치] 몬스터 처치! Lv.{player.Level} 누적 Exp={player.CurrentExp}, Gold={player.CurrentGold}",
        BattleTickEvent.PlayerDefeated => $"[{instanceId}] [부활] 플레이어 사망 → 즉시 부활",
        _ => string.Empty
    };
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter BattleEventLoggerTests`
Expected: PASS (3개 테스트)

- [ ] **Step 5: 커밋**

```bash
git add GameServer/Systems/BattleEventLogger.cs tests/GameServer.Tests/Systems/BattleEventLoggerTests.cs
git commit -m "$(cat <<'EOF'
추가: BattleEventLogger - 다중 플레이어용 이벤트 전용 로그 포맷터

스레드당 100명 규모에서 매 틱 HP 로그를 다 찍으면 콘솔이 넘치므로,
처치/사망 이벤트만 플레이어 ID 프리픽스와 함께 문자열로 포맷하는
순수 함수를 분리해 Main.cs의 샤드 루프에서 재사용할 수 있게 함.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: ShardBattleRunner 추가 (Tick 예외 격리 래퍼)

**Files:**
- Create: `GameServer/Systems/ShardBattleRunner.cs`
- Test: Create `tests/GameServer.Tests/Systems/ShardBattleRunnerTests.cs`

**Interfaces:**
- Consumes: `GameServer.Systems.BattleLoop`(기존, 생성자 `BattleLoop()`/`BattleLoop(PlayerLevelSystem)`, `internal BattleTickEvent Tick(Player, Monster, float)`), `GameServer.Entities.Player`/`Monster`(기존)
- Produces: `GameServer.Systems.ShardBattleRunner.TryTick(BattleLoop loop, Player player, Monster monster, float deltaTime, out Exception? exception) : BattleTickEvent?` — Task 4(Main.cs)에서 사용

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/GameServer.Tests/Systems/ShardBattleRunnerTests.cs` 생성:

```csharp
using GameServer.Entities;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class ShardBattleRunnerTests
{
    private static Player MakePlayer(double hp, double atk)
    {
        var player = new Player { InstanceId = "p1", AccountId = 1, Level = 1 };
        player.BaseStats.Hp = hp;
        player.BaseStats.Atk = atk;
        player.UpdateFinalStats();
        player.RestoreResources();
        return player;
    }

    private static Monster MakeMonster(double hp, double atk)
    {
        var monster = new Monster
        {
            InstanceId = "m1",
            MonsterId = 1,
            Level = 1,
            Rewards = new RewardComponent { ExpDrop = 10, GoldDrop = 5 }
        };
        monster.BaseStats.Hp = hp;
        monster.BaseStats.Atk = atk;
        monster.UpdateFinalStats();
        monster.RestoreResources();
        return monster;
    }

    [Fact]
    public void TryTick_NormalExchange_ReturnsEventAndNullException()
    {
        var player = MakePlayer(hp: 100, atk: 1000);
        var monster = MakeMonster(hp: 10, atk: 0);
        var loop = new BattleLoop();

        var result = ShardBattleRunner.TryTick(loop, player, monster, deltaTime: 1f, out var exception);

        Assert.Equal(BattleTickEvent.MonsterDefeated, result);
        Assert.Null(exception);
    }

    [Fact]
    public void TryTick_TickThrows_CatchesExceptionAndReturnsNull()
    {
        // Rewards를 null로 만들면 몬스터 처치 시 BattleLoop.Tick 내부의
        // monster.Rewards.GenerateLoot(1) 호출에서 NullReferenceException이 발생한다 —
        // 쌍 단위 예외 격리를 결정적으로 검증하기 위한 인위적 결함 주입.
        // (MakeMonster를 쓰지 않고 직접 생성 — MakeMonster는 항상 유효한 Rewards를 채워서
        // null을 넘길 방법이 없다.)
        var player = MakePlayer(hp: 100, atk: 1000);
        var monster = new Monster
        {
            InstanceId = "m1",
            MonsterId = 1,
            Level = 1,
            Rewards = null!
        };
        monster.BaseStats.Hp = 10;
        monster.BaseStats.Atk = 0;
        monster.UpdateFinalStats();
        monster.RestoreResources();
        var loop = new BattleLoop();

        var result = ShardBattleRunner.TryTick(loop, player, monster, deltaTime: 1f, out var exception);

        Assert.Null(result);
        Assert.NotNull(exception);
        Assert.IsType<NullReferenceException>(exception);
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter ShardBattleRunnerTests`
Expected: FAIL — 컴파일 오류(`ShardBattleRunner` 타입이 존재하지 않음)

- [ ] **Step 3: 최소 구현 작성**

`GameServer/Systems/ShardBattleRunner.cs` 생성:

```csharp
using GameServer.Entities;

namespace GameServer.Systems;

/// <summary>
/// 샤드 전용 스레드 안에서 <see cref="BattleLoop.Tick"/> 1회 호출을 예외로부터 격리한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> 다중 플레이어 배틀 샤드 전용 <see cref="System.Threading.Thread"/>에서
/// 동기 호출되는 것을 전제로 한다.</description></item>
/// <item><description><b>Thread Safety:</b> <see cref="TryTick"/> 자체는 공유 상태가 없어 여러 스레드가
/// 동시에 호출해도 안전하다. 다만 인자로 전달하는 <paramref name="loop"/>는 내부 상태 없이 읽기 전용
/// 조회만 수행하는 <see cref="BattleLoop"/>이어야 하며, 같은 <see cref="Player"/>/<see cref="Monster"/>
/// 인스턴스를 여러 스레드에서 동시에 대상으로 호출해서는 안 된다(<see cref="BattleLoop.Tick"/> 자체의
/// 제약과 동일).</description></item>
/// <item><description><b>Blocking 여부:</b> Non-blocking. <see cref="BattleLoop.Tick"/>과 동일하게 즉시
/// 반환한다.</description></item>
/// </list>
/// <b>[왜 이 래퍼가 필요한가]</b> 전용 <see cref="System.Threading.Thread"/>에서 처리되지 않은 예외가
/// 발생하면 .NET 런타임은 백그라운드 스레드 여부와 무관하게 프로세스 전체를 종료시킨다. 한 플레이어의
/// <see cref="BattleLoop.Tick"/> 호출에서 발생한 예외가 같은 샤드/다른 샤드의 나머지 플레이어까지 전부
/// 죽이지 않도록, 쌍(pair) 단위로 예외를 잡아 호출 측에 반환한다.
/// </remarks>
public static class ShardBattleRunner
{
    /// <summary>
    /// <paramref name="loop"/>의 <c>Tick</c>을 호출하고, 예외가 발생하면 잡아서
    /// <paramref name="exception"/>으로 반환한다.
    /// </summary>
    /// <param name="loop">전투 로직을 수행할 <see cref="BattleLoop"/>(여러 샤드가 공유 가능)</param>
    /// <param name="player">이번 틱에 참여하는 플레이어</param>
    /// <param name="monster">이번 틱에 참여하는 몬스터(플레이어 전용 인스턴스여야 함)</param>
    /// <param name="deltaTime">이번 틱에 해당하는 경과 시간(초)</param>
    /// <param name="exception"><c>Tick</c> 호출 중 발생한 예외. 정상 처리됐다면 null.</param>
    /// <returns>
    /// 정상 처리됐다면 <see cref="BattleTickEvent"/>, 예외가 발생했다면 null
    /// (이 경우 <paramref name="exception"/>이 null이 아니다).
    /// </returns>
    public static BattleTickEvent? TryTick(BattleLoop loop, Player player, Monster monster, float deltaTime, out Exception? exception)
    {
        try
        {
            var result = loop.Tick(player, monster, deltaTime);
            exception = null;
            return result;
        }
        catch (Exception ex)
        {
            exception = ex;
            return null;
        }
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter ShardBattleRunnerTests`
Expected: PASS (2개 테스트)

- [ ] **Step 5: 전체 테스트 회귀 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj`
Expected: 전부 PASS(Task 1~3 신규 테스트 포함)

- [ ] **Step 6: 커밋**

```bash
git add GameServer/Systems/ShardBattleRunner.cs tests/GameServer.Tests/Systems/ShardBattleRunnerTests.cs
git commit -m "$(cat <<'EOF'
추가: ShardBattleRunner - Tick 호출 예외를 쌍 단위로 격리

전용 Thread의 미처리 예외는 백그라운드 여부와 무관하게 프로세스
전체를 종료시킨다. 한 플레이어의 Tick 실패가 나머지 플레이어까지
죽이지 않도록 쌍(pair) 단위 try/catch 래퍼를 분리.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Main.cs를 스레드 샤딩 다중 플레이어 데모로 교체

**Files:**
- Modify: `GameServer/Main.cs` (전체 교체)

**Interfaces:**
- Consumes: `MonsterTable.CreateDefault()`/`GetById(int)`, `EquipmentTable.CreateDefault()`/`GetById(int)`, `PlayerLevelSystem.CreateDefault()`, `PlayerFactory.Create(string, int, int, PlayerLevelSystem)`, `MonsterFactory.Create(MonsterTemplate)`, `EquipmentFactory.Create(EquipmentTemplate)`, `BattleLoop(PlayerLevelSystem)`, `ShardBattleRunner.TryTick(...)`(Task 3), `BattleEventLogger.Format(...)`(Task 2) — 전부 기존 또는 앞 태스크에서 만든 시그니처 그대로
- Produces: 없음(최상위 실행 파일, 다른 코드가 참조하지 않음)

- [ ] **Step 1: Main.cs 전체 교체**

`GameServer/Main.cs`의 전체 내용을 다음으로 교체한다:

```csharp
// BigNumber는 double 별칭이다. 방치형 특성상 수치가 매우 커질 수 있어 전용 struct 도입 여지를
// 남겨둔다 — 실제 도입은 인플레이션이 double 정밀도(약 15~17자리)를 위협하는 시점에 재검토한다.
global using BigNumber = double;

using GameServer.Entities;
using GameServer.Items;
using GameServer.Systems;

// 도메인 타입 구성 예시. 다중 플레이어 배틀 스레드 샤딩 사이클(설계: docs/superpowers/specs/
// 2026-07-07-multi-player-battle-sharding-design.md)부터는 "서버에 다수의 플레이어가 동시 접속해
// 각자 독립적으로 전투를 진행"하는 상황을 시뮬레이션한다. 아직 실제 네트워크 세션 계층은 없으므로,
// ThreadCount * PlayersPerThread명의 Player/Monster 쌍을 하드코딩으로 생성해 스레드당
// PlayersPerThread명씩 나눠 맡긴다. 플레이어 간 상호작용(파티/PvP)은 없다 — 완전히 독립된 전투.

const int ThreadCount = 4;        // 조정 가능 — 총 플레이어 수 = ThreadCount * PlayersPerThread
const int PlayersPerThread = 100; // 고정(설계 문서 결정, 스레드당 100명)
var tickInterval = TimeSpan.FromMilliseconds(500);

var monsterTable = MonsterTable.CreateDefault();
var equipmentTable = EquipmentTable.CreateDefault();
var levelSystem = PlayerLevelSystem.CreateDefault();

// BattleLoop: 내부 상태가 PlayerLevelSystem(읽기 전용 마스터 테이블 조회)뿐이라 여러 샤드
// 스레드가 동시에 Tick을 호출해도 안전 — Player/Monster 인스턴스만 샤드마다 독립이면 된다.
var battleLoop = new BattleLoop(levelSystem);

var shards = Enumerable.Range(0, ThreadCount)
    .Select(shardIndex => Enumerable.Range(0, PlayersPerThread)
        .Select(i => CreatePair(shardIndex * PlayersPerThread + i))
        .ToList())
    .ToList();

foreach (var shard in shards)
{
    // Thread: 전용 스레드로 샤드를 격리한다. 샤드 루프는 Thread.Sleep으로 동기 대기하지만,
    // 스레드 풀 작업 항목이 아니라 전용 스레드라 대기 중에도 다른 작업을 막지 않는다.
    // IsBackground=true로 만들어 프로세스 종료(Ctrl+C) 시 매달리지 않게 한다.
    var thread = new Thread(() => RunShard(shard)) { IsBackground = true };
    thread.Start();
}

await Task.Delay(Timeout.Infinite); // Ctrl+C로 종료

(Player Player, Monster Monster) CreatePair(int index)
{
    var player = PlayerFactory.Create(instanceId: $"player-{index:0000}", accountId: index, level: 1, levelSystem);
    player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(4001)), SlotType.Weapon); // 낡은 검
    player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(5001)), SlotType.Armor); // 가죽 갑옷
    player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(6001)), SlotType.Accessory); // 낡은 반지
    player.UpdateFinalStats();
    player.RestoreResources();

    var monster = MonsterFactory.Create(monsterTable.GetById(2003)); // 고블린 — 플레이어마다 독립 인스턴스

    return (player, monster);
}

void RunShard(List<(Player Player, Monster Monster)> shard)
{
    var deltaTime = (float)tickInterval.TotalSeconds;
    while (true)
    {
        foreach (var (player, monster) in shard)
        {
            var result = ShardBattleRunner.TryTick(battleLoop, player, monster, deltaTime, out var exception);
            if (exception != null)
            {
                // 쌍 단위 격리: 이 예외를 여기서 삼키지 않으면 전용 스레드의 미처리 예외가
                // 프로세스 전체를 종료시킨다(백그라운드 스레드 여부 무관).
                Console.WriteLine($"[{player.InstanceId}] Tick 예외: {exception.Message}");
                continue;
            }

            if (result is not null && result.Value != BattleTickEvent.None)
            {
                Console.WriteLine(BattleEventLogger.Format(player.InstanceId, result.Value, player));
            }
        }

        Thread.Sleep(tickInterval);
    }
}
```

- [ ] **Step 2: 빌드 확인**

Run: `dotnet build IDLE_RPG.sln`
Expected: 0 error, 0 warning

- [ ] **Step 3: 테스트 회귀 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj`
Expected: 전부 PASS (Task 1~3에서 추가된 테스트 포함, `Main.cs`는 테스트 대상이 아니므로 실행 결과에 영향 없음)

- [ ] **Step 4: 수동 실행으로 육안 확인**

Run: `dotnet run --project GameServer/GameServer.csproj` (5~10초 관찰 후 Ctrl+C로 종료)
Expected:
- 여러 `[player-XXXX]` 프리픽스(예: `player-0007`, `player-0231`, `player-0399` 등 0000~0399 범위)가 뒤섞여 `[처치]`/`[부활]` 로그로 출력됨
- 매 틱 HP 상태 로그(`[전투] Player HP ...`)는 출력되지 않음
- 몇 초간 실행해도 프로세스가 죽지 않고 계속 로그가 이어짐
- Ctrl+C로 정상 종료됨

- [ ] **Step 5: 커밋**

```bash
git add GameServer/Main.cs
git commit -m "$(cat <<'EOF'
추가: 다중 플레이어 배틀 스레드 샤딩 데모로 Main.cs 교체

단일 Player-vs-Monster 데모를 스레드당 100명씩 나눈 다수의
독립 전투로 확장 - 각 샤드가 전용 Thread에서 동기 틱 루프를
돌리고, ShardBattleRunner로 예외를 격리하고 BattleEventLogger로
주요 이벤트만 로그한다.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```
