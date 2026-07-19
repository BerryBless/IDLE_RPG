using System.Globalization;
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

/// <summary><see cref="GameEventSink.SnapshotCounts"/>가 반환하는 누적 이벤트 카운터 + 마지막 보스
/// HP%/세대의 스냅샷입니다. 콘솔 상태 리포터(<see cref="ConsoleStatusReporter"/>)가 주기적으로 두 스냅샷을
/// 비교해 델타를 계산하는 용도로 쓰입니다 — NDJSON 파일이나 <see cref="GameMetrics"/>(dotnet-counters
/// 전용, 프로세스 내부에서 읽을 수 없음)를 대체하지 않습니다.</summary>
public readonly record struct GameEventCounts(
    long PlayerConnected,
    long PlayerDisconnected,
    long PlayerConnectionErrors,
    long RaidBossDefeated,
    long RaidFailed,
    long MonsterDefeated,
    long PlayerDefeated,
    long TickExceptions,
    double LastBossHpPercent,
    int LastBossGeneration);

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

    // long + Interlocked.Increment/Read: GameMetrics(System.Diagnostics.Metrics)의 Counter<long>는
    // dotnet-counters 같은 외부 프로세스가 pull로만 구독 가능하고 이 프로세스 안에서는 값을 읽을 수
    // 없다. ConsoleStatusReporter가 5초 주기로 "지난 구간에 몇 건 있었는지" 델타를 계산하려면 프로세스
    // 내부에서 읽을 수 있는 누적치가 별도로 필요해, Interlocked 카운터를 여기 나란히 둔다. Increment는
    // lock-free CAS 루프라 다수 샤드/레이드 스레드가 동시에 호출해도 컨텐션 없이 원자적으로 누적된다.
    private long _playerConnectedTotal;
    private long _playerDisconnectedTotal;
    private long _playerConnectionErrorTotal;
    private long _raidBossDefeatedTotal;
    private long _raidFailedTotal;
    private long _monsterDefeatedTotal;
    private long _playerDefeatedTotal;
    private long _tickExceptionTotal;

    // double/int + Volatile.Write/Read: 보스 HP%·세대는 "가장 최근 값 하나"만 표시하면 되는 단순 상태라
    // CAS 기반 Interlocked.Increment는 필요 없다 — 컴파일러/CPU의 재정렬로 다른 스레드가 절반만 갱신된
    // 값을 보는 것만 막으면 충분하므로, 더 가벼운 메모리 배리어만 세우는 Volatile.Read/Write를 쓴다.
    private double _lastBossHpPercent;
    private int _lastBossGeneration;

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

    // CultureInfo.InvariantCulture: 커스텀 포맷 문자열은 현재 스레드 문화권의 달력을 따르므로,
    // 지정하지 않으면 비그레고리력 문화권(예: th-TH의 불기)에서 연도가 어긋나 machine-readable
    // NDJSON ts 필드가 깨진다.
    private static string Ts(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture);

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

    /// <summary>소켓 연결(임시 플레이어 배정) 이벤트를 NDJSON 한 줄로 포맷한다(순수 함수, 테스트 대상).</summary>
    internal static string PlayerConnectedLine(DateTime ts, string playerId, int level)
        => JsonSerializer.Serialize(new GameEventLine(Ts(ts), "PlayerConnected", playerId, level),
            GameEventJsonContext.Default.GameEventLine);

    /// <summary>소켓 연결 해제 이벤트를 NDJSON 한 줄로 포맷한다(순수 함수, 테스트 대상).</summary>
    internal static string PlayerDisconnectedLine(DateTime ts, string playerId)
        => JsonSerializer.Serialize(new GameEventLine(Ts(ts), "PlayerDisconnected", playerId),
            GameEventJsonContext.Default.GameEventLine);

    /// <summary>연결 중 발생한 오류(OnClientError)를 NDJSON 한 줄로 포맷한다(순수 함수, 테스트 대상).</summary>
    internal static string PlayerConnectionErrorLine(DateTime ts, string playerId, string error)
        => JsonSerializer.Serialize(new GameEventLine(Ts(ts), "PlayerConnectionError", playerId, Error: error),
            GameEventJsonContext.Default.GameEventLine);

    /// <summary>몬스터 처치 이벤트를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordMonsterDefeated(string playerId, int level, BigNumber exp, BigNumber gold)
    {
        _metrics.MonsterDefeated();
        Interlocked.Increment(ref _monsterDefeatedTotal);
        _lineChannel.Writer.TryWrite(MonsterDefeatedLine(DateTime.UtcNow, playerId, level, exp, gold));
    }

    /// <summary>플레이어 사망(즉시 부활) 이벤트를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordPlayerDefeated(string playerId)
    {
        _metrics.PlayerDefeated();
        Interlocked.Increment(ref _playerDefeatedTotal);
        _lineChannel.Writer.TryWrite(PlayerDefeatedLine(DateTime.UtcNow, playerId));
    }

    /// <summary>레이드 보스 처치 이벤트를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordRaidBossDefeated(int contributorCount)
    {
        _metrics.RaidBossDefeated();
        Interlocked.Increment(ref _raidBossDefeatedTotal);
        _lineChannel.Writer.TryWrite(RaidBossDefeatedLine(DateTime.UtcNow, contributorCount));
    }

    /// <summary>레이드 실패(제한시간 초과) 이벤트를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordRaidFailed()
    {
        _metrics.RaidFailed();
        Interlocked.Increment(ref _raidFailedTotal);
        _lineChannel.Writer.TryWrite(RaidFailedLine(DateTime.UtcNow));
    }

    /// <summary>Tick 처리 중 발생한 예외를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordTickException(string playerId, Exception exception)
    {
        _metrics.TickException();
        Interlocked.Increment(ref _tickExceptionTotal);
        _lineChannel.Writer.TryWrite(TickExceptionLine(DateTime.UtcNow, playerId, exception.Message));
    }

    /// <summary>레이드 보스 현재 HP 비율과 세대를 기록한다. 연속 상태라 NDJSON 라인은 남기지 않고,
    /// 게이지(<see cref="GameMetrics"/>)와 <see cref="SnapshotCounts"/>용 최신값만 갱신한다.</summary>
    /// <param name="percent">보스 현재 HP 비율(0~100).</param>
    /// <param name="generation">현재 진행 중인 레이드 시도 세대(<see cref="RaidEncounter"/> 기준).</param>
    public void RecordRaidBossHpPercent(double percent, int generation)
    {
        _metrics.RaidBossHpPercent(percent);
        Volatile.Write(ref _lastBossHpPercent, percent);
        Volatile.Write(ref _lastBossGeneration, generation);
    }

    /// <summary>소켓 연결(임시 플레이어 배정) 이벤트를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordPlayerConnected(string playerId, int level)
    {
        _metrics.PlayerConnected();
        Interlocked.Increment(ref _playerConnectedTotal);
        _lineChannel.Writer.TryWrite(PlayerConnectedLine(DateTime.UtcNow, playerId, level));
    }

    /// <summary>소켓 연결 해제 이벤트를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordPlayerDisconnected(string playerId)
    {
        _metrics.PlayerDisconnected();
        Interlocked.Increment(ref _playerDisconnectedTotal);
        _lineChannel.Writer.TryWrite(PlayerDisconnectedLine(DateTime.UtcNow, playerId));
    }

    /// <summary>연결 중 발생한 오류(OnClientError)를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordPlayerConnectionError(string playerId, Exception exception)
    {
        _metrics.PlayerConnectionError();
        Interlocked.Increment(ref _playerConnectionErrorTotal);
        _lineChannel.Writer.TryWrite(PlayerConnectionErrorLine(DateTime.UtcNow, playerId, exception.Message));
    }

    /// <summary>현재까지 누적된 이벤트 카운터와 마지막 보스 HP%/세대의 스냅샷을 반환한다.</summary>
    /// <returns>호출 시점의 누적 카운터 + 마지막 보스 상태 스냅샷.</returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Safety:</b> Thread-safe. 필드마다 개별적으로 <see cref="Interlocked.Read(ref long)"/>
    /// 또는 <see cref="Volatile.Read{T}(ref T)"/>로 원자 읽기하므로 다수 생산자 스레드와 동시에 호출해도
    /// 안전하다. 다만 여러 필드를 하나의 <see cref="GameEventCounts"/>로 묶는 과정 자체는 원자적이지
    /// 않다 — 표시용 근사치(콘솔 상태 줄)이므로 필드 간 미세한 시점 차이는 무해하다.</description></item>
    /// <item><description><b>Blocking:</b> Non-blocking, 즉시 반환.</description></item>
    /// <item><description><b>Memory Allocation:</b> Zero-allocation. <see cref="GameEventCounts"/>는
    /// record struct라 힙 할당 없이 스택에서 반환된다.</description></item>
    /// </list>
    /// </remarks>
    public GameEventCounts SnapshotCounts() => new(
        Interlocked.Read(ref _playerConnectedTotal),
        Interlocked.Read(ref _playerDisconnectedTotal),
        Interlocked.Read(ref _playerConnectionErrorTotal),
        Interlocked.Read(ref _raidBossDefeatedTotal),
        Interlocked.Read(ref _raidFailedTotal),
        Interlocked.Read(ref _monsterDefeatedTotal),
        Interlocked.Read(ref _playerDefeatedTotal),
        Interlocked.Read(ref _tickExceptionTotal),
        Volatile.Read(ref _lastBossHpPercent),
        Volatile.Read(ref _lastBossGeneration));

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
