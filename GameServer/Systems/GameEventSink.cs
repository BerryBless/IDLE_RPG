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

    /// <summary>소켓 연결(임시 플레이어 배정) 이벤트를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordPlayerConnected(string playerId, int level)
    {
        _metrics.PlayerConnected();
        _lineChannel.Writer.TryWrite(PlayerConnectedLine(DateTime.UtcNow, playerId, level));
    }

    /// <summary>소켓 연결 해제 이벤트를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordPlayerDisconnected(string playerId)
    {
        _metrics.PlayerDisconnected();
        _lineChannel.Writer.TryWrite(PlayerDisconnectedLine(DateTime.UtcNow, playerId));
    }

    /// <summary>연결 중 발생한 오류(OnClientError)를 기록한다: 카운터 1 증가 + NDJSON 한 줄 기록.</summary>
    public void RecordPlayerConnectionError(string playerId, Exception exception)
    {
        _metrics.PlayerConnectionError();
        _lineChannel.Writer.TryWrite(PlayerConnectionErrorLine(DateTime.UtcNow, playerId, exception.Message));
    }

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
