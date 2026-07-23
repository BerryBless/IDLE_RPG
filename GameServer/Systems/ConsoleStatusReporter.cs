using System.Globalization;
using ServerLib.Interface;

namespace GameServer.Systems;

/// <summary>
/// GameServer 콘솔에 N초 주기로 접속자 수·레이드 보스 상태·최근 구간 이벤트 델타를 한 줄로 요약해
/// 출력하는 경량 리포터. run-local.bat 등으로 여러 서버를 동시에 띄웠을 때 GameServer 창이 살아있는지,
/// 지금 무슨 일이 벌어지고 있는지 한눈에 볼 수 있게 하기 위한 것이다.
/// </summary>
/// <remarks>
/// <b>[의도적으로 지키는 것]</b> 2026-07-07 관측성 전환 정책(게임 이벤트는 콘솔이 아닌
/// <see cref="GameEventSink"/>의 NDJSON 파일로만 기록)을 뒤집지 않는다 — 매 이벤트를 개별 출력하는
/// 대신, 이미 <see cref="GameEventSink"/>에 누적된 카운터를 주기적으로 스냅샷/차분(delta)해 "집계된"
/// 한 줄만 남긴다. 이는 <see cref="TelemetryPublisher"/>가 1초 <see cref="PeriodicTimer"/>로 상태를
/// 집계·브로드캐스트하는 것과 같은 방향의 패턴이다 — 다만 이쪽은 네트워크가 아니라 콘솔 출력이 대상이다.
/// <br/><br/>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Hot path 영향 없음:</b> 콘솔 출력은 이 클래스의 단일 백그라운드 루프에서만
/// 발생한다 — <c>GameEventSink.Record*</c> 호출부(다수 I/O/액터 스레드)는 Interlocked 카운터 증가만
/// 하고 콘솔에 전혀 관여하지 않는다. 출력 빈도는 이벤트 발생량과 무관하게 <see cref="_interval"/>로
/// 상한이 걸리고, 아무 일도 없는 유휴 구간에는 <see cref="_heartbeatInterval"/> 주기로만 1줄 찍는다.</description></item>
/// <item><description><b>Thread Safety:</b> <see cref="Start"/>는 인스턴스당 정확히 1회만 호출해야
/// 한다(내부 루프의 <c>previous</c>/<c>lastPrintedAt</c>은 그 단일 루프 전용 지역 변수라 락이 불필요하다).
/// <see cref="Diff"/>/<see cref="HasActivity"/>/<see cref="FormatLine"/>은 순수 함수라 스레드 무관하게 안전하다.</description></item>
/// <item><description><b>Blocking 여부:</b> Non-blocking. <see cref="PeriodicTimer.WaitForNextTickAsync(CancellationToken)"/>와
/// <see cref="TextWriter.WriteLineAsync(string?)"/>에서만 대기하며, 둘 다 호출 스레드를 점유하지 않는다.</description></item>
/// </list>
/// </remarks>
public sealed class ConsoleStatusReporter
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(60);

    // IReadOnlyList<IServerListener>: 용량 모드에서 게임 리스너가 P개(멀티포트)로 늘어나므로 전 포트의
    // ActiveSessionCount/TotalRejectedConnections를 합산해야 콘솔의 players=/rejected가 전역 접속 수가 된다.
    // (TelemetryPublisher와 동일 합산 정책 — 두 관측 경로가 같은 값을 보이도록 통일, 코드리뷰 Medium.)
    private readonly IReadOnlyList<IServerListener> _listeners;
    private readonly GameEventSink _sink;
    private readonly TextWriter _output;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _heartbeatInterval;

    /// <summary>콘솔 상태 리포터를 생성한다.</summary>
    /// <param name="listeners">접속자 수·거부된 연결 수를 읽어올 게임 리스너들(멀티포트 시 P개). 전 포트를 합산한다.</param>
    /// <param name="sink">이벤트 누적 카운터와 마지막 보스 HP%/세대를 스냅샷해올 이벤트 싱크.</param>
    /// <param name="output">출력 대상. 프로덕션에서는 <see cref="Console.Out"/>, 테스트에서는
    /// <see cref="StringWriter"/>를 주입해 실제 콘솔 없이 검증할 수 있다.</param>
    /// <param name="interval">상태 줄 출력 주기. 생략 시 5초.</param>
    /// <param name="heartbeatInterval">활동이 전혀 없는 유휴 구간에서도 "살아있음"을 알리는 최소 출력
    /// 주기. 생략 시 60초.</param>
    public ConsoleStatusReporter(IReadOnlyList<IServerListener> listeners, GameEventSink sink, TextWriter output,
        TimeSpan? interval = null, TimeSpan? heartbeatInterval = null)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        if (listeners.Count == 0) throw new ArgumentException("최소 하나의 리스너가 필요합니다.", nameof(listeners));
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(output);
        _listeners = listeners;
        _sink = sink;
        _output = output;
        _interval = interval ?? DefaultInterval;
        _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
    }

    /// <summary>백그라운드 태스크로 상태 리포트 루프를 시작한다.</summary>
    /// <param name="cancellationToken">서버 종료 시 취소되는 수명 토큰.</param>
    /// <returns>루프 태스크. 호출자가 fire-and-forget으로 버려도 무방하다 — 정상 취소는 내부에서
    /// 삼키고, 그 외 예외는 그대로 전파되어 UnobservedTaskException으로 관측 가능하다.</returns>
    /// <remarks><b>Blocking 여부:</b> Non-blocking, 즉시 반환(루프 자체는 <see cref="Task.Run(Func{Task})"/>로
    /// 스레드 풀에 위임).</remarks>
    public Task Start(CancellationToken cancellationToken) => Task.Run(() => RunAsync(cancellationToken), cancellationToken);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // PeriodicTimer: 단일 OS 타이머 핸들을 재사용해 WaitForNextTickAsync를 반복 호출해도 매번 새
        // 타이머를 등록/해제하지 않는다(TelemetryPublisher.PublishLoopAsync와 동일 근거).
        using var timer = new PeriodicTimer(_interval);
        var previous = _sink.SnapshotCounts();
        DateTime? lastPrintedAt = null;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var current = _sink.SnapshotCounts();
                var delta = Diff(previous, current);
                previous = current;

                // 전 포트 합산: 단일 포트면 루프 1회. TelemetryPublisher.PublishLoopAsync와 동일 방식.
                int connectedCount = 0;
                long rejectedConnections = 0;
                for (int i = 0; i < _listeners.Count; i++)
                {
                    connectedCount += _listeners[i].ActiveSessionCount;
                    rejectedConnections += _listeners[i].TotalRejectedConnections;
                }
                bool idle = connectedCount == 0 && !HasActivity(delta);
                var now = DateTime.UtcNow;

                // 첫 틱은 항상 출력(리포터가 실제로 살아있음을 즉시 확인시켜준다). 이후 유휴 구간은
                // heartbeatInterval이 지날 때까지 침묵해 반복 출력이 콘솔/터미널 I/O를 잠식하지 않게 한다.
                bool shouldPrint = !idle || lastPrintedAt is null || now - lastPrintedAt.Value >= _heartbeatInterval;
                if (!shouldPrint)
                {
                    continue;
                }

                string line = FormatLine(now, connectedCount, rejectedConnections,
                    delta, current.LastBossHpPercent, current.LastBossGeneration, idle, _interval);
                await _output.WriteLineAsync(line);
                lastPrintedAt = now;
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료(서버 수명 토큰 취소).
        }
    }

    /// <summary>두 스냅샷 사이의 누적 카운터 차이를 계산한다(순수 함수, 테스트 대상).</summary>
    /// <remarks><see cref="GameEventCounts.LastBossHpPercent"/>/<see cref="GameEventCounts.LastBossGeneration"/>은
    /// 델타 개념이 없는 "최신값" 필드라 차분하지 않고 <paramref name="current"/> 값을 그대로 통과시킨다.</remarks>
    internal static GameEventCounts Diff(GameEventCounts previous, GameEventCounts current) => new(
        current.PlayerConnected - previous.PlayerConnected,
        current.PlayerDisconnected - previous.PlayerDisconnected,
        current.PlayerConnectionErrors - previous.PlayerConnectionErrors,
        current.RaidBossDefeated - previous.RaidBossDefeated,
        current.RaidFailed - previous.RaidFailed,
        current.MonsterDefeated - previous.MonsterDefeated,
        current.PlayerDefeated - previous.PlayerDefeated,
        current.TickExceptions - previous.TickExceptions,
        current.LastBossHpPercent,
        current.LastBossGeneration);

    /// <summary>델타 카운터 중 하나라도 0이 아니면 true(순수 함수, 테스트 대상).</summary>
    internal static bool HasActivity(GameEventCounts delta) =>
        delta.PlayerConnected != 0 || delta.PlayerDisconnected != 0 || delta.PlayerConnectionErrors != 0 ||
        delta.RaidBossDefeated != 0 || delta.RaidFailed != 0 || delta.MonsterDefeated != 0 ||
        delta.PlayerDefeated != 0 || delta.TickExceptions != 0;

    /// <summary>콘솔 상태 한 줄을 포맷한다(순수 함수, 테스트 대상).</summary>
    /// <param name="nowUtc">출력 시각(UTC) — <see cref="GameEventSink"/>의 NDJSON 타임스탬프와 동일하게
    /// UTC로 통일해 두 로그를 사람이 대조하기 쉽게 한다.</param>
    /// <param name="connectedCount">현재 접속자 수.</param>
    /// <param name="rejectedConnections">누적 거부된 연결 수(0이면 줄에서 생략).</param>
    /// <param name="delta">직전 스냅샷 대비 이번 구간의 이벤트 델타.</param>
    /// <param name="bossHpPercent">마지막으로 관측된 레이드 보스 HP 비율(0~100).</param>
    /// <param name="generation">마지막으로 관측된 레이드 시도 세대.</param>
    /// <param name="idle">활동이 없어 heartbeat로 출력되는 줄인지 여부.</param>
    /// <param name="window">이번 구간의 길이(활동 줄의 "(last Ns)" 표기에 사용, idle 줄에는 표시하지 않음).</param>
    internal static string FormatLine(DateTime nowUtc, int connectedCount, long rejectedConnections,
        GameEventCounts delta, double bossHpPercent, int generation, bool idle, TimeSpan window)
    {
        // CultureInfo.InvariantCulture: GameEventSink.Ts()와 동일한 이유 — 현재 스레드 문화권에 따라
        // 시각 포맷이 달라지는 것을 막는다.
        string ts = nowUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        string boss = $"boss={bossHpPercent.ToString("0.0", CultureInfo.InvariantCulture)}%(gen {generation})";

        if (idle)
        {
            return $"[{ts}] players={connectedCount} {boss} idle";
        }

        string rejectedSuffix = rejectedConnections > 0 ? $" rejected {rejectedConnections}" : string.Empty;
        int windowSeconds = (int)window.TotalSeconds;
        return $"[{ts}] players={connectedCount} {boss} | " +
               $"+conn {delta.PlayerConnected} -disc {delta.PlayerDisconnected} " +
               $"kills {delta.RaidBossDefeated} fails {delta.RaidFailed} err {delta.PlayerConnectionErrors}" +
               $"{rejectedSuffix} (last {windowSeconds}s)";
    }
}
