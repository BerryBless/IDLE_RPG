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
