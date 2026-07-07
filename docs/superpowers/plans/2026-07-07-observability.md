# 콘솔 출력 제거 → 메트릭 + NDJSON 로그 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 게임 서버의 두 콘솔 출력 경로(`Main.cs` 샤딩/레이드 데모, `BattleLoop.RunAsync` 단일 페어 데모)를 완전히 제거하고, `System.Diagnostics.Metrics` 기반 실시간 카운터/게이지(`dotnet-counters`로 관찰)와 NDJSON 파일 로그로 대체한다.

**Architecture:** `GameMetrics`(Meter/Counter/Gauge 래퍼)와 `GameEventSink`(메트릭 갱신 + NDJSON 파일 기록을 통합한, 기존 `logChannel` 패턴을 재사용 클래스로 승격한 것)를 신설한다. `BattleLoop`/`RaidEncounter`/`Main.cs`가 `Console.WriteLine` 대신 `GameEventSink`의 `Record*` 메서드를 호출하도록 배선하고, 콘솔 전용이었던 `BattleEventLogger`/`RaidEventLogger`는 삭제한다.

**Tech Stack:** .NET 10 (`net10.0`), `System.Diagnostics.Metrics`(내장, 추가 NuGet 불필요), `System.Text.Json`(source-gen), `System.Threading.Channels`, xUnit(`GameServer.Tests`).

## Global Constraints

- 콘솔 출력을 **완전히** 제거한다 — `Main.cs`의 샤딩/레이드 경로와 `BattleLoop.RunAsync`의 단일 페어 경로 둘 다
- 메트릭: 카운터 5개(`game.monster.defeated`, `game.player.defeated`, `game.raid.boss_defeated`, `game.raid.failed`, `game.tick.exceptions`) + 게이지 1개(`game.raid.boss_hp_percent`, 단위 `%`). Meter 이름은 프로덕션 기본값 `"IdleRpg.GameServer"`, 버전 `"1.0.0"` — `dotnet-counters monitor -p <pid> --counters IdleRpg.GameServer`로 관찰 가능해야 함
- NDJSON 파일 로그: 기존 콘솔에 찍던 이벤트(몬스터 처치·플레이어 부활·레이드 보스 처치·레이드 실패·Tick 예외)만 한 줄에 JSON 객체 하나로 기록. 레이드 보스 HP%는 연속 상태이므로 게이지로만 기록하고 NDJSON에는 남기지 않음
- `BattleTickEvent` enum은 이미 `GameServer/Systems/BattleLoop.cs`에 정의돼 있다(이동 불필요) — `BattleEventLogger.cs`/`RaidEventLogger.cs`는 콘솔용 문자열 포맷팅이 유일한 역할이었으므로 **전체 삭제**
- `BattleLoop.RunAsync`의 신규 `sink` 파라미터는 **옵션(기본값 null)이며 맨 뒤에 위치**해야 한다 — 기존 테스트 `RunAsync(player, monster, TimeSpan.FromMilliseconds(1), cts.Token)`의 4번째 positional 인자가 계속 `cancellationToken`에 바인딩되어야 함. `sink`가 null이면 아무 것도 기록하지 않는다(no-op)
- 테스트에서 `GameMetrics`/`MeterListener`를 쓸 때는 **테스트 전용 고유 Meter 이름**을 써야 한다 — Meter 이름이 하드코딩된 `"IdleRpg.GameServer"` 하나로 고정되면, xUnit이 서로 다른 테스트 클래스를 병렬 실행할 때 같은 이름의 Meter가 동시에 여러 개 존재해 `MeterListener`가 다른 테스트의 측정값까지 수신하는 크로스토크가 생길 수 있다 — 그래서 `GameMetrics` 생성자는 `meterName` 파라미터를 받는다(기본값이 프로덕션 이름, 테스트는 `Guid.NewGuid()` 기반 고유 이름 주입)
- 신규 `public` 타입/메서드는 XML `<remarks>`에 Thread Safety/Memory Allocation/Blocking 여부를 명시. `Channel<T>`/`Meter`/`Gauge`/파일 스트림 선언에는 "왜 이 타입을 골랐는가"를 내부 동작 메커니즘 근거로 인라인 주석에 남길 것
- 타깃 프레임워크 `net10.0`, `GameServer.csproj`는 이미 `GameServer.Tests`에 `InternalsVisibleTo`를 부여함(추가 설정 불필요)

---

### Task 1: GameMetrics

**Files:**
- Create: `GameServer/Systems/GameMetrics.cs`
- Test: Create `tests/GameServer.Tests/Systems/GameMetricsTests.cs`

**Interfaces:**
- Consumes: 없음(신규, `System.Diagnostics.Metrics`만 사용)
- Produces: `GameServer.Systems.GameMetrics`(public sealed class, `IDisposable`) — 생성자 `GameMetrics(string meterName = "IdleRpg.GameServer")`, 메서드 `MonsterDefeated()`/`PlayerDefeated()`/`RaidBossDefeated()`/`RaidFailed()`/`TickException()`(전부 `void`, 인자 없음) 및 `RaidBossHpPercent(double percent)` — Task 2(`GameEventSink`)에서 그대로 사용

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/GameServer.Tests/Systems/GameMetricsTests.cs` 생성:

```csharp
using System.Diagnostics.Metrics;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class GameMetricsTests
{
    [Fact]
    public void AllInstruments_RecordExpectedValues()
    {
        // 병렬 실행 중인 다른 테스트의 GameMetrics와 Meter 이름이 겹치지 않도록 테스트마다 고유
        // 이름을 준다 — 안 그러면 MeterListener가 다른 테스트의 측정값까지 주워 카운트가 어긋난다.
        var meterName = $"Test.GameMetrics.{Guid.NewGuid()}";
        using var metrics = new GameMetrics(meterName);

        var counters = new Dictionary<string, long>();
        double gaugeValue = 0;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == meterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            counters[instrument.Name] = counters.GetValueOrDefault(instrument.Name) + measurement);
        listener.SetMeasurementEventCallback<double>((_, measurement, _, _) => gaugeValue = measurement);
        listener.Start();

        metrics.MonsterDefeated();
        metrics.PlayerDefeated();
        metrics.RaidBossDefeated();
        metrics.RaidFailed();
        metrics.TickException();
        metrics.RaidBossHpPercent(42.5);

        Assert.Equal(1, counters["game.monster.defeated"]);
        Assert.Equal(1, counters["game.player.defeated"]);
        Assert.Equal(1, counters["game.raid.boss_defeated"]);
        Assert.Equal(1, counters["game.raid.failed"]);
        Assert.Equal(1, counters["game.tick.exceptions"]);
        Assert.Equal(42.5, gaugeValue);
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter GameMetricsTests`
Expected: FAIL — 컴파일 오류(`GameMetrics` 타입이 존재하지 않음)

- [ ] **Step 3: 최소 구현 작성**

`GameServer/Systems/GameMetrics.cs` 생성:

```csharp
using System.Diagnostics.Metrics;

namespace GameServer.Systems;

/// <summary>dotnet-counters로 실시간 관측 가능한 게임 이벤트 계측기 묶음.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="Counter{T}.Add"/>/<see cref="Gauge{T}.Record"/>는
/// 내부적으로 Interlocked 기반이라 다수 샤드 스레드가 동시에 호출해도 락 경합 없이 안전하다.</description></item>
/// <item><description><b>Blocking 여부:</b> Non-blocking, 즉시 반환.</description></item>
/// <item><description><b>Memory Allocation:</b> 계측기는 생성자에서 1회만 할당되고, 이후 <c>Add</c>/<c>Record</c> 호출은
/// 추가 힙 할당이 없다.</description></item>
/// </list>
/// </remarks>
public sealed class GameMetrics : IDisposable
{
    // Meter: dotnet-counters가 이 이름으로 프로세스 외부에서 카운터/게이지를 구독(pull)한다.
    // 이름을 안정 식별자로 고정해 --counters 필터가 항상 매칭되게 한다. 테스트에서는 병렬 실행 중인
    // 다른 테스트와 Meter 이름이 겹쳐 MeterListener가 크로스토크하지 않도록 고유 이름을 주입받는다.
    private readonly Meter _meter;

    private readonly Counter<long> _monsterDefeated;
    private readonly Counter<long> _playerDefeated;
    private readonly Counter<long> _raidBossDefeated;
    private readonly Counter<long> _raidFailed;
    private readonly Counter<long> _tickExceptions;

    // Gauge<double>: .NET 9+ push 게이지. ObservableGauge(콜백 pull)와 달리, 보스 HP를 유일하게
    // 읽는 레이드 액터 스레드가 그 시점 값을 직접 Record한다(폴링 타이밍 불일치 없음).
    private readonly Gauge<double> _raidBossHpPercent;

    /// <summary>지정한 이름의 Meter로 계측기 묶음을 생성한다.</summary>
    /// <param name="meterName">Meter 식별자. 프로덕션 기본값은 <c>"IdleRpg.GameServer"</c>이며,
    /// 테스트에서는 고유 이름을 주입해 병렬 테스트 간 Meter 크로스토크를 피한다.</param>
    public GameMetrics(string meterName = "IdleRpg.GameServer")
    {
        _meter = new Meter(meterName, "1.0.0");
        _monsterDefeated = _meter.CreateCounter<long>("game.monster.defeated", "events");
        _playerDefeated = _meter.CreateCounter<long>("game.player.defeated", "events");
        _raidBossDefeated = _meter.CreateCounter<long>("game.raid.boss_defeated", "events");
        _raidFailed = _meter.CreateCounter<long>("game.raid.failed", "events");
        _tickExceptions = _meter.CreateCounter<long>("game.tick.exceptions", "events");
        _raidBossHpPercent = _meter.CreateGauge<double>("game.raid.boss_hp_percent", "%");
    }

    /// <summary>몬스터 처치 이벤트 1건을 카운트한다.</summary>
    public void MonsterDefeated() => _monsterDefeated.Add(1);

    /// <summary>플레이어 사망(즉시 부활) 이벤트 1건을 카운트한다.</summary>
    public void PlayerDefeated() => _playerDefeated.Add(1);

    /// <summary>레이드 보스 처치 이벤트 1건을 카운트한다.</summary>
    public void RaidBossDefeated() => _raidBossDefeated.Add(1);

    /// <summary>레이드 제한시간 초과(실패) 이벤트 1건을 카운트한다.</summary>
    public void RaidFailed() => _raidFailed.Add(1);

    /// <summary>Tick 처리 중 발생한 예외 1건을 카운트한다.</summary>
    public void TickException() => _tickExceptions.Add(1);

    /// <summary>레이드 보스의 현재 HP 비율(0~100)을 게이지에 기록한다.</summary>
    public void RaidBossHpPercent(double percent) => _raidBossHpPercent.Record(percent);

    /// <summary>내부 Meter를 해제한다.</summary>
    public void Dispose() => _meter.Dispose();
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter GameMetricsTests`
Expected: PASS (1개 테스트)

- [ ] **Step 5: 전체 테스트 회귀 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj`
Expected: 기존 114개 + 신규 1개 = 115개 전부 PASS

- [ ] **Step 6: 커밋**

```bash
git add GameServer/Systems/GameMetrics.cs tests/GameServer.Tests/Systems/GameMetricsTests.cs
git commit -m "$(cat <<'EOF'
추가: GameMetrics - dotnet-counters로 관측 가능한 게임 이벤트 계측기

콘솔 출력을 없애는 대신 System.Diagnostics.Metrics 기반 카운터 5개
(처치/사망/레이드처치/레이드실패/Tick예외) + 게이지 1개(레이드 보스
HP%)를 신설 - dotnet-counters monitor로 실시간 관측 가능.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: GameEventSink

**Files:**
- Create: `GameServer/Systems/GameEventSink.cs`
- Test: Create `tests/GameServer.Tests/Systems/GameEventSinkTests.cs`

**Interfaces:**
- Consumes: `GameServer.Systems.GameMetrics`(Task 1 — 생성자, `MonsterDefeated()`/`PlayerDefeated()`/`RaidBossDefeated()`/`RaidFailed()`/`TickException()`/`RaidBossHpPercent(double)`)
- Produces: `GameServer.Systems.GameEventSink`(public sealed class, `IAsyncDisposable`) — 생성자 `GameEventSink(TextWriter writer, GameMetrics? metrics = null, bool ownsWriter = false)`, 정적 팩토리 `GameEventSink.CreateFile(string path) : GameEventSink`, 메서드 `RecordMonsterDefeated(string playerId, int level, BigNumber exp, BigNumber gold)`, `RecordPlayerDefeated(string playerId)`, `RecordRaidBossDefeated(int contributorCount)`, `RecordRaidFailed()`, `RecordTickException(string playerId, Exception exception)`, `RecordRaidBossHpPercent(double percent)`, `CompleteWriting()`, `DisposeAsync() : ValueTask` — Task 3(`BattleLoop`), Task 4(`RaidEncounter`), Task 5(`Main.cs`)에서 그대로 사용

- [ ] **Step 1: 실패하는 테스트 작성**

`tests/GameServer.Tests/Systems/GameEventSinkTests.cs` 생성:

```csharp
using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class GameEventSinkTests
{
    [Fact]
    public void MonsterDefeatedLine_ShapesJson()
    {
        var ts = new DateTime(2026, 7, 7, 15, 20, 1, DateTimeKind.Utc);

        var line = GameEventSink.MonsterDefeatedLine(ts, "player-0042", 3, 41, 22);

        Assert.Equal(
            """{"ts":"2026-07-07T15:20:01Z","type":"MonsterDefeated","playerId":"player-0042","level":3,"exp":41,"gold":22}""",
            line);
    }

    [Fact]
    public void PlayerDefeatedLine_OmitsUnusedFields()
    {
        var ts = new DateTime(2026, 7, 7, 15, 20, 2, DateTimeKind.Utc);

        var line = GameEventSink.PlayerDefeatedLine(ts, "player-0187");

        Assert.Equal("""{"ts":"2026-07-07T15:20:02Z","type":"PlayerDefeated","playerId":"player-0187"}""", line);
    }

    [Fact]
    public void RaidBossDefeatedLine_OmitsNullFields()
    {
        var ts = new DateTime(2026, 7, 7, 15, 20, 3, DateTimeKind.Utc);

        var line = GameEventSink.RaidBossDefeatedLine(ts, 100);

        Assert.Equal("""{"ts":"2026-07-07T15:20:03Z","type":"RaidBossDefeated","contributors":100}""", line);
    }

    [Fact]
    public void RaidFailedLine_HasOnlyTsAndType()
    {
        var ts = new DateTime(2026, 7, 7, 15, 20, 4, DateTimeKind.Utc);

        var line = GameEventSink.RaidFailedLine(ts);

        Assert.Equal("""{"ts":"2026-07-07T15:20:04Z","type":"RaidFailed"}""", line);
    }

    [Fact]
    public void TickExceptionLine_IncludesErrorMessage()
    {
        var ts = new DateTime(2026, 7, 7, 15, 20, 5, DateTimeKind.Utc);

        var line = GameEventSink.TickExceptionLine(ts, "player-0007", "Object reference not set");

        Assert.Equal(
            """{"ts":"2026-07-07T15:20:05Z","type":"TickException","playerId":"player-0007","error":"Object reference not set"}""",
            line);
    }

    [Fact]
    public void RecordMonsterDefeated_IncrementsMetricAndWritesLine()
    {
        // 병렬 실행 중인 다른 테스트와 Meter 이름이 겹치지 않도록 고유 이름을 쓴다(GameMetricsTests와 동일 이유).
        var metrics = new GameMetrics($"Test.GameEventSink.{Guid.NewGuid()}");
        long sum = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "game.monster.defeated")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => sum += measurement);
        listener.Start();

        using var stringWriter = new StringWriter();
        var sink = new GameEventSink(stringWriter, metrics);

        sink.RecordMonsterDefeated("player-0001", 1, 6, 8);

        Assert.Equal(1, sum);
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter GameEventSinkTests`
Expected: FAIL — 컴파일 오류(`GameEventSink` 타입이 존재하지 않음)

- [ ] **Step 3: 최소 구현 작성**

`GameServer/Systems/GameEventSink.cs` 생성:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace GameServer.Systems;

/// <summary>NDJSON 파일 로그 한 줄의 스키마. 이벤트 종류마다 쓰는 필드가 달라 nullable로 통일하고,
/// 직렬화 시 null 필드는 생략해(<see cref="JsonIgnoreCondition.WhenWritingNull"/>) 이벤트별 shape을 만든다.</summary>
internal readonly record struct GameEventLine(
    string Ts, string Type,
    string? PlayerId = null, int? Level = null,
    double? Exp = null, double? Gold = null,
    int? Contributors = null, string? Error = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GameEventLine))]
internal partial class GameEventJsonContext : JsonSerializerContext
{
}

/// <summary>메트릭 갱신 + NDJSON 파일 기록을 통합한 이벤트 싱크.</summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <c>Record*</c> 메서드는 Thread-safe하다(다수 샤드
/// 스레드가 동시에 생산자로 호출 가능). 파일 쓰기는 내부 단일 소비자 태스크에서만 수행된다.</description></item>
/// <item><description><b>Blocking 여부:</b> <c>Record*</c>는 Non-blocking — 무경계 채널의 TryWrite는
/// 항상 즉시 성공한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 호출마다 JSON 문자열 1개를 새로 할당해 채널에 넣는다.</description></item>
/// </list>
/// </remarks>
public sealed class GameEventSink : IAsyncDisposable
{
    // Channel<string>: lock-free MPSC. 다수 샤드/레이드 액터(생산자) → 단일 파일 소비자 경로에서
    // 파일 I/O 락 경합 없이 라인을 전달한다(Main.cs의 기존 logChannel과 동일 근거).
    // SingleReader=true로 소비자 최적화 경로 사용, SingleWriter=false로 다중 생산자(여러 샤드) 허용.
    private readonly Channel<string> _lineChannel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly TextWriter _writer;
    private readonly bool _ownsWriter;
    private readonly GameMetrics _metrics;
    private readonly Task _consumerTask;

    /// <summary>임의의 <see cref="TextWriter"/>로 싱크를 만든다. 테스트에서는 <see cref="StringWriter"/>나
    /// <see cref="TextWriter.Null"/>로 실제 파일 I/O 없이 사용할 수 있다.</summary>
    /// <param name="writer">NDJSON 라인을 쓸 대상</param>
    /// <param name="metrics">사용할 계측기 묶음. 생략 시 프로덕션 기본 이름으로 새로 생성한다.</param>
    /// <param name="ownsWriter"><see cref="DisposeAsync"/>에서 <paramref name="writer"/>도 함께
    /// 해제할지 여부(파일 소유 시 true, 외부에서 관리하는 writer면 false)</param>
    public GameEventSink(TextWriter writer, GameMetrics? metrics = null, bool ownsWriter = false)
    {
        _writer = writer;
        _ownsWriter = ownsWriter;
        _metrics = metrics ?? new GameMetrics();
        _consumerTask = Task.Run(RunConsumerAsync);
    }

    /// <summary>지정 경로를 truncate 모드로 열어 파일 싱크를 만든다(매 실행 새로 시작).</summary>
    /// <param name="path">NDJSON 로그를 기록할 파일 경로. 상위 디렉터리가 없으면 생성한다.</param>
    public static GameEventSink CreateFile(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // StreamWriter(append:false): 매 실행 truncate해 이전 실행 로그와 섞이지 않게 한다.
        var streamWriter = new StreamWriter(path, append: false);
        return new GameEventSink(streamWriter, ownsWriter: true);
    }

    private async Task RunConsumerAsync()
    {
        var reader = _lineChannel.Reader;
        // WaitToReadAsync 바깥 + TryRead 안쪽 배치 드레인 후 1회 Flush: 라인마다 flush하는 것보다
        // syscall이 적으면서도 실시간으로 파일을 tail할 수 있다(기존 logConsumerTask의 파일판).
        while (await reader.WaitToReadAsync())
        {
            while (reader.TryRead(out var line))
            {
                await _writer.WriteLineAsync(line);
            }
            await _writer.FlushAsync();
        }
    }

    private static string Ts(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss'Z'");

    /// <summary>몬스터 처치 이벤트를 NDJSON 한 줄로 포맷한다(순수 함수, 테스트 대상).</summary>
    internal static string MonsterDefeatedLine(DateTime ts, string playerId, int level, double exp, double gold)
        => JsonSerializer.Serialize(new GameEventLine(Ts(ts), "MonsterDefeated", playerId, level, exp, gold),
            GameEventJsonContext.Default.GameEventLine);

    /// <summary>플레이어 사망(즉시 부활) 이벤트를 NDJSON 한 줄로 포맷한다(순수 함수, 테스트 대상).</summary>
    internal static string PlayerDefeatedLine(DateTime ts, string playerId)
        => JsonSerializer.Serialize(new GameEventLine(Ts(ts), "PlayerDefeated", playerId),
            GameEventJsonContext.Default.GameEventLine);

    /// <summary>레이드 보스 처치 이벤트를 NDJSON 한 줄로 포맷한다(순수 함수, 테스트 대상).</summary>
    internal static string RaidBossDefeatedLine(DateTime ts, int contributors)
        => JsonSerializer.Serialize(new GameEventLine(Ts(ts), "RaidBossDefeated", Contributors: contributors),
            GameEventJsonContext.Default.GameEventLine);

    /// <summary>레이드 실패(제한시간 초과) 이벤트를 NDJSON 한 줄로 포맷한다(순수 함수, 테스트 대상).</summary>
    internal static string RaidFailedLine(DateTime ts)
        => JsonSerializer.Serialize(new GameEventLine(Ts(ts), "RaidFailed"),
            GameEventJsonContext.Default.GameEventLine);

    /// <summary>Tick 처리 중 발생한 예외를 NDJSON 한 줄로 포맷한다(순수 함수, 테스트 대상).</summary>
    internal static string TickExceptionLine(DateTime ts, string playerId, string error)
        => JsonSerializer.Serialize(new GameEventLine(Ts(ts), "TickException", playerId, Error: error),
            GameEventJsonContext.Default.GameEventLine);

    /// <summary>몬스터 처치 이벤트를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordMonsterDefeated(string playerId, int level, BigNumber exp, BigNumber gold)
    {
        _metrics.MonsterDefeated();
        _lineChannel.Writer.TryWrite(MonsterDefeatedLine(DateTime.UtcNow, playerId, level, exp, gold));
    }

    /// <summary>플레이어 사망(즉시 부활) 이벤트를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordPlayerDefeated(string playerId)
    {
        _metrics.PlayerDefeated();
        _lineChannel.Writer.TryWrite(PlayerDefeatedLine(DateTime.UtcNow, playerId));
    }

    /// <summary>레이드 보스 처치 이벤트를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordRaidBossDefeated(int contributorCount)
    {
        _metrics.RaidBossDefeated();
        _lineChannel.Writer.TryWrite(RaidBossDefeatedLine(DateTime.UtcNow, contributorCount));
    }

    /// <summary>레이드 실패(제한시간 초과) 이벤트를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordRaidFailed()
    {
        _metrics.RaidFailed();
        _lineChannel.Writer.TryWrite(RaidFailedLine(DateTime.UtcNow));
    }

    /// <summary>Tick 처리 중 발생한 예외를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordTickException(string playerId, Exception exception)
    {
        _metrics.TickException();
        _lineChannel.Writer.TryWrite(TickExceptionLine(DateTime.UtcNow, playerId, exception.Message));
    }

    /// <summary>레이드 보스 현재 HP 비율을 게이지에 기록한다. 연속 상태라 NDJSON 라인은 남기지 않는다.</summary>
    public void RecordRaidBossHpPercent(double percent) => _metrics.RaidBossHpPercent(percent);

    /// <summary>더 이상 라인이 생성되지 않음을 알린다(채널 완료 신호).</summary>
    public void CompleteWriting() => _lineChannel.Writer.TryComplete();

    /// <summary>드레인 순서: 완료 신호 → 소비자 종료 대기 → 파일 flush/close → 계측기 해제.</summary>
    public async ValueTask DisposeAsync()
    {
        CompleteWriting();
        await _consumerTask;
        if (_ownsWriter)
        {
            await _writer.DisposeAsync();
        }
        _metrics.Dispose();
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter GameEventSinkTests`
Expected: PASS (6개 테스트)

- [ ] **Step 5: 전체 테스트 회귀 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj`
Expected: 전부 PASS (115개 + 신규 6개 = 121개)

- [ ] **Step 6: 커밋**

```bash
git add GameServer/Systems/GameEventSink.cs tests/GameServer.Tests/Systems/GameEventSinkTests.cs
git commit -m "$(cat <<'EOF'
추가: GameEventSink - 메트릭 갱신 + NDJSON 파일 로그 통합 싱크

기존 Main.cs의 logChannel+logConsumerTask(콘솔 전용) 패턴을 재사용
가능한 클래스로 승격 - 목적지를 콘솔에서 파일로 바꾸고, 이벤트마다
GameMetrics 카운터/게이지도 함께 갱신한다. 이벤트 종류가 다른 필드를
쓰므로 nullable 필드를 가진 단일 레코드 + WhenWritingNull로 통일.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: BattleLoop 콘솔 제거

**순서 주의(2026-07-07 실행 중 발견): `BattleEventLogger.cs`는 이 태스크에서 삭제하지 않는다.**
`BattleEventLogger.Format`은 `BattleLoop.cs`(이 태스크가 고침)와 `Main.cs`(Task 5가 고침) 두 곳에서
호출된다. 이 태스크에서 파일을 지우면 아직 손대지 않은 `Main.cs`가 참조를 잃어 Task 3~5 사이에
빌드가 깨지는 창이 생긴다. 그래서 `BattleEventLogger.cs`의 실제 삭제는 **Task 5**로 옮겼다(그때
`Main.cs`가 마지막 참조를 없애므로 안전하게 지울 수 있다). 이 태스크는 `BattleLoop.cs` 안의 호출만
없앤다 — 파일 자체는 그대로 둔다.

**Files:**
- Modify: `GameServer/Systems/BattleLoop.cs`

**주의:** `GameServer/Systems/BattleEventLogger.cs`와
`tests/GameServer.Tests/Systems/BattleEventLoggerTests.cs`는 이 태스크에서 건드리지 않는다
(둘 다 그대로 둔다) — 위 순서 주의 참고. `BattleEventLogger.cs`는 여전히 `Main.cs`가 참조하고
있으므로 컴파일이 깨지지 않게 살려둔다.

**Interfaces:**
- Consumes: `GameServer.Systems.GameEventSink`(Task 2 — `RecordMonsterDefeated`/`RecordPlayerDefeated`)
- Produces: `BattleLoop.RunAsync(Player, Monster, TimeSpan?, CancellationToken, GameEventSink?)`(마지막 파라미터 신규, 옵션) — 다른 태스크는 이 시그니처를 참조하지 않음(단일 페어 데모/테스트 전용 경로)

- [ ] **Step 1: 기존 테스트가 계속 통과하는지 확인(회귀 기준선)**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter BattleLoopTests`
Expected: PASS (수정 전 현재 상태 확인용 — 아래 변경 후에도 그대로 통과해야 함)

- [ ] **Step 2: `BattleLoop.cs` 수정 — `LogTick`을 `GameEventSink` 기반으로 교체**

`GameServer/Systems/BattleLoop.cs`에서 `RunAsync` 메서드 시그니처를 다음으로 교체(기존 XML 문서
주석은 유지하되 `sink` 파라미터 설명 추가):

```csharp
    /// <param name="sink">이벤트를 기록할 싱크. 생략(null)하면 아무 것도 기록하지 않는다.</param>
    public async Task RunAsync(Player player, Monster monster, TimeSpan? tickInterval = null,
        CancellationToken cancellationToken = default, GameEventSink? sink = null)
    {
        var interval = tickInterval ?? DefaultTickInterval;
        var deltaTime = (float)interval.TotalSeconds;

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = Tick(player, monster, deltaTime);
            LogTick(result, player, sink);

            if (interval > TimeSpan.Zero)
            {
                await Task.Delay(interval);
            }
        }
    }
```

그리고 `LogTick` 메서드 전체(기존 `Console.WriteLine` 2곳 포함)를 다음으로 완전히 교체:

```csharp
    /// <summary>이번 틱의 결과를 <paramref name="sink"/>에 기록한다. sink가 null이면 아무 것도 기록하지 않는다.</summary>
    /// <remarks>
    /// 코드리뷰(2026-07-07 관측성 전환): 콘솔 출력을 제거하고 <see cref="GameEventSink"/> 기반
    /// 메트릭+NDJSON 로그로 대체했다. HP 상태(<see cref="BattleTickEvent.None"/>)는 이벤트가 아니라
    /// 매 틱 연속 상태이므로 기록하지 않는다.
    /// </remarks>
    private static void LogTick(BattleTickEvent result, Player player, GameEventSink? sink)
    {
        if (sink is null)
        {
            return;
        }

        switch (result)
        {
            case BattleTickEvent.MonsterDefeated:
                sink.RecordMonsterDefeated(player.InstanceId, player.Level, player.CurrentExp, player.CurrentGold);
                break;
            case BattleTickEvent.PlayerDefeated:
                sink.RecordPlayerDefeated(player.InstanceId);
                break;
        }
    }
```

- [ ] **Step 4: 빌드 확인**

Run: `dotnet build IDLE_RPG.sln`
Expected: 0 error, 0 warning (`BattleEventLogger.cs`는 아직 존재하고 `Main.cs`가 참조하므로
빌드는 계속 성공해야 한다 — 이 태스크는 `BattleLoop.cs`의 호출만 없앨 뿐 그 파일 자체는 지우지 않는다)

- [ ] **Step 5: 테스트 통과 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter BattleLoopTests`
Expected: PASS — 기존 `RunAsync_WithCancellationToken_StopsWithoutHanging`을 포함해 전부 통과
(이 테스트는 `sink`를 안 넘기므로 `new BattleLoop().RunAsync(player, monster,
TimeSpan.FromMilliseconds(1), cts.Token)` 호출이 그대로 컴파일되고, `LogTick`이 `sink is null`
분기로 아무 것도 안 하며 조용히 지나간다)

- [ ] **Step 6: 전체 테스트 회귀 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj`
Expected: 121개 전부 PASS(이 태스크는 아무 테스트도 삭제하지 않는다 — `BattleEventLoggerTests.cs`
삭제는 Task 5로 미뤘다)

- [ ] **Step 7: 커밋**

```bash
git add GameServer/Systems/BattleLoop.cs
git commit -m "$(cat <<'EOF'
리팩토링: BattleLoop 콘솔 출력 제거, GameEventSink로 전환

RunAsync의 단일 페어 데모 경로가 GameEventSink를 옵션으로 받아
Console.WriteLine 대신 메트릭+NDJSON으로 기록하도록 변경. 콘솔 문자열
포맷팅이 유일한 역할이었던 BattleEventLogger는 아직 Main.cs가 참조 중이라
이 태스크에서는 지우지 않는다(Task 5에서 Main.cs 전환과 함께 삭제 예정).

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: RaidEncounter 콘솔 제거 + RaidEventLogger 삭제

**Files:**
- Modify: `GameServer/Systems/RaidEncounter.cs`
- Modify: `tests/GameServer.Tests/Systems/RaidEncounterTests.cs`
- Delete: `GameServer/Systems/RaidEventLogger.cs`

**Interfaces:**
- Consumes: `GameServer.Systems.GameEventSink`(Task 2 — `RecordRaidBossDefeated(int)`/`RecordRaidFailed()`/`RecordRaidBossHpPercent(double)`)
- Produces: `RaidEncounter.RunAsync(GameEventSink sink, CancellationToken cancellationToken)`(시그니처 변경 — 기존 `RunAsync(ChannelWriter<string>, CancellationToken)`을 대체) — Task 5(`Main.cs`)에서 사용

- [ ] **Step 1: `RaidEncounterTests.cs`의 `RunAsync` 테스트 수정**

`tests/GameServer.Tests/Systems/RaidEncounterTests.cs`에서 `RunAsync_WithCancellationToken_StopsWithoutHanging`
테스트를 다음으로 교체(기존 `Channel.CreateUnbounded<string>()`/`logChannel.Writer` 사용 부분을
`GameEventSink`로 대체):

```csharp
    [Fact]
    public async Task RunAsync_WithCancellationToken_StopsWithoutHanging()
    {
        var boss = MakeBoss(hp: 1_000_000, def: 0, expDrop: 1, goldDrop: 1);
        var raid = new RaidEncounter(boss, TimeSpan.FromSeconds(30));
        var sink = new GameEventSink(TextWriter.Null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        raid.SubmitDamage("p1", 100); // 취소 전에 채널에 미리 넣어 ReadAllAsync가 최소 1회 순회하게 함

        await raid.RunAsync(sink, cts.Token);

        // 취소 후 정상 반환된 것 자체가 핵심 검증(무한 대기에 갇히지 않음, 스레드도 점유하지 않음).
        // 제출한 데미지가 실제로 적용됐는지도 함께 확인.
        Assert.Equal(999_900, boss.FinalStats.CurrentHp);
    }
```

파일 상단에 `using System.Threading.Channels;`가 이제 필요 없어졌다면(다른 테스트가 `Channel`을
안 쓰면) 제거해도 되지만, 컴파일이 깨지지 않는 한 그대로 둬도 무방하다 — 이 스텝의 핵심은
테스트 본문 교체다.

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter RunAsync_WithCancellationToken_StopsWithoutHanging`
Expected: FAIL — 컴파일 오류(`RaidEncounter.RunAsync(GameEventSink, CancellationToken)` 오버로드가
아직 없어 `GameEventSink` 인자를 받는 시그니처가 없음)

- [ ] **Step 3: `RaidEncounter.cs` 수정 — `RunAsync`/`Emit`을 `GameEventSink` 기반으로 교체**

`GameServer/Systems/RaidEncounter.cs`에서 `RunAsync` 메서드 전체를 다음으로 교체:

```csharp
    /// <summary>피해 채널을 소비하며 순수 판정 코어를 순차 구동하는 액터 루프.</summary>
    /// <param name="sink">레이드 이벤트(처치/실패)와 보스 HP% 게이지를 기록할 싱크</param>
    /// <param name="cancellationToken">협조적 취소 토큰(Main의 <c>CancellationTokenSource</c>와 동일 토큰 전달)</param>
    /// <remarks>
    /// <b>Blocking 여부:</b> Non-blocking. <c>ReadAllAsync</c>의 <c>await</c>에서만 대기하며 호출
    /// 스레드를 점유하지 않는다. 코드리뷰(2026-07-07 관측성 전환): 콘솔 로그 채널 대신
    /// <see cref="GameEventSink"/>로 메트릭+NDJSON을 기록한다. 액터가 <c>boss.CurrentHp</c>의 유일한
    /// 리더이므로, 매 스텝 후 HP% 게이지를 기록하는 것도 이 루프에서만 안전하게 할 수 있다.
    /// </remarks>
    public async Task RunAsync(GameEventSink sink, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _damageChannel.Reader.ReadAllAsync(cancellationToken))
            {
                Emit(ApplyDamage(request), sink);
                Emit(CheckDeadline(_clock()), sink); // 처치 직후라면 위에서 이미 데드라인이 재시작된 뒤라 안전
                sink.RecordRaidBossHpPercent(BossHpPercent());
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 취소 종료
        }
        finally
        {
            _rewardChannel.Writer.TryComplete();
        }
    }

    /// <summary>보스의 현재 HP 비율(0~100)을 계산한다. MaxHp가 0 이하인 방어적 경우 0을 반환한다.</summary>
    private double BossHpPercent()
        => _boss.FinalStats.MaxHp <= 0 ? 0 : _boss.FinalStats.CurrentHp / _boss.FinalStats.MaxHp * 100.0;
```

그리고 `Emit` 메서드 전체를 다음으로 교체(보상 grant를 `_rewardChannel`에 넣는 게임 로직은
그대로 유지 — 로깅만 교체):

```csharp
    private void Emit(RaidStepResult step, GameEventSink sink)
    {
        if (step.Event is RaidEventType.None or RaidEventType.BossDamaged)
        {
            return;
        }

        if (step.Event == RaidEventType.BossDefeated)
        {
            sink.RecordRaidBossDefeated(step.Grants.Count);
        }
        else if (step.Event == RaidEventType.RaidFailed)
        {
            sink.RecordRaidFailed();
        }

        foreach (var grant in step.Grants)
        {
            _rewardChannel.Writer.TryWrite(grant);
        }
    }
```

파일 상단의 `using System.Threading.Channels;`는 `_damageChannel`/`_rewardChannel` 필드 선언에
여전히 필요하므로 그대로 둔다.

- [ ] **Step 4: `RaidEventLogger.cs` 삭제**

```bash
rm GameServer/Systems/RaidEventLogger.cs
```

- [ ] **Step 5: 빌드 확인**

Run: `dotnet build IDLE_RPG.sln`
Expected: 0 error, 0 warning

- [ ] **Step 6: 테스트 통과 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj --filter RaidEncounterTests`
Expected: PASS (전체 8개 — 7개는 무변경, `RunAsync` 1개는 수정됨)

- [ ] **Step 7: 전체 테스트 회귀 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj`
Expected: 118개 전부 PASS(순증감 없음 — 삭제 없고 기존 테스트 1개 수정)

- [ ] **Step 8: 커밋**

```bash
git add GameServer/Systems/RaidEncounter.cs tests/GameServer.Tests/Systems/RaidEncounterTests.cs
git rm GameServer/Systems/RaidEventLogger.cs
git commit -m "$(cat <<'EOF'
리팩토링: RaidEncounter 콘솔 출력 제거, GameEventSink로 전환

RunAsync가 ChannelWriter<string> 대신 GameEventSink를 받아 처치/실패
이벤트를 메트릭+NDJSON으로 기록하고, 액터 루프 안에서만 안전한 보스
HP% 게이지도 매 스텝 갱신한다. 콘솔 문자열 포맷팅이 유일한 역할이던
RaidEventLogger는 삭제. 보상 grant 전달 로직은 그대로 유지.

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: Main.cs 통합 + .gitignore

**Files:**
- Modify: `GameServer/Main.cs` (전체 교체)
- Modify: `.gitignore`
- Delete: `GameServer/Systems/BattleEventLogger.cs` (Task 3에서 미뤄둔 삭제 — 이제 `Main.cs`가
  마지막 참조를 없애 안전하게 지울 수 있음)
- Delete: `tests/GameServer.Tests/Systems/BattleEventLoggerTests.cs`

**Interfaces:**
- Consumes: `GameEventSink.CreateFile(string)`(Task 2), `GameEventSink.DisposeAsync()`(Task 2), `BattleLoop.RunAsync`는 사용하지 않음(샤딩 경로는 `ShardBattleRunner.TryTick` 그대로), `RaidEncounter.RunAsync(GameEventSink, CancellationToken)`(Task 4) — 전부 기존 또는 앞 태스크에서 만든 시그니처 그대로
- Produces: 없음(최상위 실행 파일)

- [ ] **Step 1: `.gitignore`에 `logs/` 추가**

`.gitignore` 파일 끝에 한 줄 추가:

```
logs/
```

- [ ] **Step 2: `Main.cs` 전체 교체**

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
//
// 2026-07-07 공유 레이드 보스 사이클(docs/superpowers/plans/2026-07-07-raid-boss.md): RaidShardIndex
// 샤드의 플레이어들은 개인 몬스터 대신 하나의 공유 보스를 함께 공격한다.
//
// 2026-07-07 관측성 전환(docs/superpowers/plans/2026-07-07-observability.md): 콘솔 출력을 완전히
// 제거하고 GameEventSink(System.Diagnostics.Metrics 카운터/게이지 + NDJSON 파일 로그)로 대체했다.
// dotnet-counters monitor -p <pid> --counters IdleRpg.GameServer 로 실시간 관측 가능.

const int ThreadCount = 4;        // 조정 가능 — 총 플레이어 수 = ThreadCount * PlayersPerThread
const int PlayersPerThread = 100; // 고정(설계 문서 결정, 스레드당 100명)
const int RaidShardIndex = 0;     // 샤드 0만 공유 레이드 보스 참여, 나머지는 기존 개인 전투 유지
var tickInterval = TimeSpan.FromMilliseconds(500);
var raidTimeLimit = TimeSpan.FromSeconds(30); // 이 시간 내에 못 잡으면 레이드 실패(데모용 상수)

var monsterTable = MonsterTable.CreateDefault();
var equipmentTable = EquipmentTable.CreateDefault();
var levelSystem = PlayerLevelSystem.CreateDefault();

// BattleLoop: 내부 상태가 PlayerLevelSystem(읽기 전용 마스터 테이블 조회)뿐이라 여러 샤드
// 스레드가 동시에 Tick을 호출해도 안전 — Player/Monster 인스턴스만 샤드마다 독립이면 된다.
var battleLoop = new BattleLoop(levelSystem);

// GameEventSink: 다수 샤드/레이드 액터(생산자)가 Record*로 메트릭+NDJSON 라인을 밀어넣고, 내부
// 단일 소비자 태스크가 파일에 flush한다(기존 logChannel+logConsumerTask를 재사용 클래스로 승격).
await using var sink = GameEventSink.CreateFile(Path.Combine("logs", "game-events.ndjson"));

// CancellationTokenSource: Ctrl+C(SIGINT) 기본 동작인 즉시 프로세스 종료 대신, 각 샤드 전용
// 스레드가 다음 대기 지점(WaitHandle.WaitOne)에서 스스로 루프를 빠져나가는 협조적 취소 신호로
// 바꾼다(코드리뷰 2026-07-07 아키텍처 High 수정 — 이전에는 while(true)뿐이라 정상 종료 수단이
// 프로세스 강제 종료밖에 없었다). 레이드 액터도 동일 토큰으로 종료된다.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // 기본 강제 종료를 막고, 대신 취소 토큰으로 각 샤드가 정리할 시간을 준다
    cts.Cancel();
};

// 레이드 보스: MonsterFactory.Create가 스폰 시 1회 UpdateFinalStats+RestoreResources를 마친
// 상태로 반환한다. 이후에는 RaidEncounter의 절대 규칙에 따라 이 인스턴스에 Update/UpdateFinalStats를
// 다시는 호출하지 않는다 — 샤드 스레드가 동시에 읽는 Def/CombatTraits를 재기록하면 값이 같아도
// 데이터 레이스가 되기 때문이다.
var raidBoss = MonsterFactory.Create(monsterTable.GetById(7001));
var raid = new RaidEncounter(raidBoss, raidTimeLimit);

// Task.Run: 레이드 액터를 전용 백그라운드 태스크로 띄운다. RunAsync는 await 지점에서만
// 대기하므로 스레드 풀 스레드를 점유하지 않는다.
var raidActorTask = Task.Run(() => raid.RunAsync(sink, cts.Token));

for (int shardIndex = 0; shardIndex < ThreadCount; shardIndex++)
{
    if (shardIndex == RaidShardIndex)
    {
        var raidPlayers = Enumerable.Range(0, PlayersPerThread)
            .Select(i => CreateRaidPlayer(shardIndex * PlayersPerThread + i))
            .ToList();
        // Thread: 레이드 샤드도 개인 전투 샤드와 동일한 전용 스레드 격리 근거(WaitHandle.WaitOne 동기
        // 대기, IsBackground 안전망)를 그대로 따른다.
        var raidThread = new Thread(() => RunRaidShard(raidPlayers, raidBoss, raid, cts.Token)) { IsBackground = true };
        raidThread.Start();
    }
    else
    {
        // shard: for 루프 변수 shardIndex를 그대로 클로저에서 캡처하면 안 된다 — foreach와 달리
        // for 루프 변수는 반복마다 새로 스코프되지 않고 전체 루프에서 단일 변수를 공유하므로,
        // 스레드가 실제로 시작되는 시점(비동기)에는 루프가 이미 끝나버린 값을 참조하게 된다(실측:
        // ArgumentOutOfRangeException). 이 로컬 변수는 반복마다 새로 계산·선언되므로 클로저가
        // 이번 반복의 값을 안전하게 캡처한다. 레이드 샤드(RaidShardIndex)는 개인 몬스터가 필요
        // 없으므로 이 목록에서 처음부터 제외한다(만들어놓고 버리는 낭비 방지).
        var shard = Enumerable.Range(0, PlayersPerThread)
            .Select(i => CreatePair(shardIndex * PlayersPerThread + i))
            .ToList();
        // Thread: 전용 스레드로 샤드를 격리한다. 샤드 루프는 WaitHandle.WaitOne으로 동기 대기하지만,
        // 스레드 풀 작업 항목이 아니라 전용 스레드라 대기 중에도 다른 작업을 막지 않는다.
        // IsBackground=true는 정상 취소 경로를 놓치는 비정상 상황에서도 프로세스가 매달리지 않게
        // 하는 안전망이다(정상 경로는 CancellationToken으로 각 샤드가 스스로 종료한다).
        var thread = new Thread(() => RunShard(shard, cts.Token)) { IsBackground = true };
        thread.Start();
    }
}

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C로 정상 종료 진입 — 아래에서 싱크를 닫고 남은 메트릭/로그를 flush한다.
}

// 레이드 액터를 먼저 기다린다: 액터가 sink에 마지막 처치/실패 라인을 쓸 수 있으므로,
// sink를 닫기 전에 액터가 끝나야 그 로그가 유실되지 않는다.
await raidActorTask;
// await using으로 선언했으므로 sink.DisposeAsync()는 스코프 종료 시 자동 호출된다.

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

Player CreateRaidPlayer(int index)
{
    // CreatePair와 동일한 장비 세팅이나, 개인 몬스터를 만들지 않는다(레이드 샤드는 공유 보스만 공격).
    var player = PlayerFactory.Create(instanceId: $"player-{index:0000}", accountId: index, level: 1, levelSystem);
    player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(4001)), SlotType.Weapon);
    player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(5001)), SlotType.Armor);
    player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(6001)), SlotType.Accessory);
    player.UpdateFinalStats();
    player.RestoreResources();
    return player;
}

void RunShard(List<(Player Player, Monster Monster)> shard, CancellationToken cancellationToken)
{
    var deltaTime = (float)tickInterval.TotalSeconds;
    while (!cancellationToken.IsCancellationRequested)
    {
        foreach (var (player, monster) in shard)
        {
            var result = ShardBattleRunner.TryTick(battleLoop, player, monster, deltaTime, out var exception);
            if (exception != null)
            {
                // 쌍 단위 격리: 이 예외를 여기서 삼키지 않으면 전용 스레드의 미처리 예외가
                // 프로세스 전체를 종료시킨다(백그라운드 스레드 여부 무관).
                sink.RecordTickException(player.InstanceId, exception);
                continue;
            }

            switch (result!.Value)
            {
                case BattleTickEvent.MonsterDefeated:
                    sink.RecordMonsterDefeated(player.InstanceId, player.Level, player.CurrentExp, player.CurrentGold);
                    break;
                case BattleTickEvent.PlayerDefeated:
                    sink.RecordPlayerDefeated(player.InstanceId);
                    break;
            }
        }

        // 취소 토큰을 감시하며 대기 — Thread.Sleep과 달리 취소 시 즉시 깨어나 루프를 빠져나간다.
        cancellationToken.WaitHandle.WaitOne(tickInterval);
    }
}

void RunRaidShard(List<Player> players, Monster sharedBoss, RaidEncounter encounter, CancellationToken cancellationToken)
{
    var deltaTime = (float)tickInterval.TotalSeconds;
    var playersById = players.ToDictionary(p => p.InstanceId);
    var rewardReader = encounter.RewardReader;

    while (!cancellationToken.IsCancellationRequested)
    {
        // 1) 액터가 되돌려준 보상 grant를 이 스레드(소유 스레드)에서 적용한다 — Player.AddExp/AddGold
        //    변경은 그 Player를 소유한 이 스레드에서만 수행(단일 소유 원칙).
        while (rewardReader.TryRead(out var grant))
        {
            if (playersById.TryGetValue(grant.PlayerInstanceId, out var owner))
            {
                owner.AddExp(grant.Exp);
                owner.AddGold(grant.Gold);
            }
        }

        // 2) 각 플레이어의 보스 피해를 계산해 fire-and-forget으로 전송한다. 보스의 CurrentHp/IsAlive는
        //    절대 읽지 않는다 — 액터 스레드가 쓰는 값과의 레이스를 피하기 위해서다.
        foreach (var player in players)
        {
            try
            {
                player.Update(deltaTime); // 자기 Player만 갱신(보스는 Update하지 않음 — 위 절대 규칙 참고)
                var damage = BattleManager.Instance.CalcFinalDamage(player, sharedBoss); // 보스의 불변 Def/ArmorPen만 읽음
                encounter.SubmitDamage(player.InstanceId, damage);
            }
            catch (Exception ex)
            {
                sink.RecordTickException(player.InstanceId, ex);
            }
        }

        cancellationToken.WaitHandle.WaitOne(tickInterval);
    }
}
```

- [ ] **Step 3: `BattleEventLogger.cs`/`BattleEventLoggerTests.cs` 삭제**

Task 3에서 미뤄둔 삭제. `Main.cs`가 방금 위 Step 2에서 `BattleEventLogger.Format` 호출을 없앴고,
`BattleLoop.cs`도 Task 3에서 이미 안 쓰므로, 이제 이 파일을 참조하는 곳이 전혀 없어 안전하게 지울 수 있다.

```bash
rm GameServer/Systems/BattleEventLogger.cs
rm tests/GameServer.Tests/Systems/BattleEventLoggerTests.cs
```

- [ ] **Step 4: 빌드 확인**

Run: `dotnet build IDLE_RPG.sln`
Expected: 0 error, 0 warning (`BattleEventLogger` 참조가 모두 제거됐는지 컴파일러가 확인해줌)

- [ ] **Step 5: 테스트 회귀 확인**

Run: `dotnet test tests/GameServer.Tests/GameServer.Tests.csproj`
Expected: 121개 - 3개(삭제된 `BattleEventLoggerTests`) = 118개 전부 PASS

- [ ] **Step 6: 수동 실행으로 콘솔 무출력 + 파일/메트릭 확인**

Run: `dotnet run --project GameServer/GameServer.csproj` (10~40초 관찰 후 종료 — `raidTimeLimit`이
30초이므로 최소 한 번의 레이드 처치/실패 이벤트를 보려면 30초 이상 관찰 권장)

Expected:
- 콘솔에 **아무 출력도 없어야 한다**(이전 사이클까지 나오던 `[처치]`/`[부활]`/`[레이드]` 로그가
  전부 사라짐)
- `logs/game-events.ndjson` 파일이 생성되고, 각 줄이 유효한 JSON이며 `"type"` 필드가
  `MonsterDefeated`/`PlayerDefeated`/`RaidBossDefeated`/`RaidFailed`/`TickException` 중 하나인지 확인
- (선택) 실행 중 별도 터미널에서 `dotnet-counters monitor -p <pid> --counters IdleRpg.GameServer`를
  실행해 5개 카운터 + 1개 게이지가 실시간으로 갱신되는지 확인

- [ ] **Step 7: 커밋**

```bash
git add GameServer/Main.cs .gitignore
git rm GameServer/Systems/BattleEventLogger.cs tests/GameServer.Tests/Systems/BattleEventLoggerTests.cs
git commit -m "$(cat <<'EOF'
추가: Main.cs 콘솔 출력을 GameEventSink(메트릭+NDJSON)로 전환

logChannel+logConsumerTask(콘솔 전용)를 GameEventSink.CreateFile로
교체 - 모든 샤드/레이드 액터/예외 경로가 이제 메트릭 카운터·게이지를
갱신하고 logs/game-events.ndjson에 NDJSON 한 줄씩 기록한다. 콘솔에는
아무 것도 출력하지 않는다. logs/를 .gitignore에 추가. 콘솔 문자열
포맷팅이 유일한 역할이었던 BattleEventLogger는 이제 어디서도 쓰이지
않아 삭제(BattleTickEvent enum은 이미 BattleLoop.cs에 있어 이동 불필요).

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```
