using GameServer.Entities;

namespace GameServer.Systems;

/// <summary>한 틱의 전투 교환에서 발생한 사망 이벤트. <see cref="BattleLoop.RunAsync"/>가 로그 출력에 사용한다.</summary>
public enum BattleTickEvent
{
    /// <summary>이번 틱에 특별한 사망 이벤트가 없었다.</summary>
    None,

    /// <summary>몬스터가 처치되어 보상을 지급하고 재등장했다.</summary>
    MonsterDefeated,

    /// <summary>플레이어가 사망해 즉시 부활했다.</summary>
    PlayerDefeated
}

/// <summary>
/// 단일 <see cref="Player"/>와 단일 <see cref="Monster"/> 사이의 라운드제 전투를 반복 실행하는 루프.
/// 웨이브·다중 몬스터·스킬 자동 시전은 다루지 않는다(각각 <c>MonsterSpawner</c>/<c>Stage</c>·스킬
/// 시스템 도입 시 별도 사이클에서 확장 예정).
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> <see cref="RunAsync"/>는 <c>await</c> 지점(<see cref="Task.Delay(TimeSpan)"/>)마다
/// 호출 스레드를 반환하므로, 여러 전투를 동시에 <c>RunAsync</c>해도 전투당 스레드를 점유하지 않는다
/// (코드리뷰 2026-07-06 H2 수정 — 이전에는 <c>Thread.Sleep</c> 동기 블로킹이라 전투당 스레드 1개가
/// 항상 묶여 다중 전투 확장 시 스레드 풀 기아를 유발했다).</description></item>
/// <item><description><b>Blocking 여부:</b> Non-blocking. <c>await</c> 동안 호출 스레드를 점유하지 않는다.
/// 취소되지 않는 한 반환하지 않는 <see cref="Task"/>를 반환한다 — 기본 호출(토큰 미전달)은 의도적으로
/// 진짜 무한 루프다.</description></item>
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 동일 <see cref="Player"/>/<see cref="Monster"/>
/// 인스턴스를 여러 스레드에서 동시에 <see cref="Tick"/>하면 안 된다.</description></item>
/// </list>
/// </remarks>
public sealed class BattleLoop
{
    /// <summary><see cref="RunAsync"/> 호출 시 <c>tickInterval</c>을 지정하지 않으면 사용하는 기본 간격.</summary>
    private static readonly TimeSpan DefaultTickInterval = TimeSpan.FromMilliseconds(500);

    private readonly PlayerLevelSystem _levelSystem;

    /// <summary>하드코딩된 기본 레벨 테이블(<see cref="PlayerLevelSystem.CreateDefault"/>)을 사용하는 루프를 생성한다.</summary>
    public BattleLoop() : this(PlayerLevelSystem.CreateDefault())
    {
    }

    /// <summary>
    /// 지정한 레벨업 시스템을 사용하는 루프를 생성한다.
    /// </summary>
    /// <param name="levelSystem">몬스터 처치 시 레벨업 판정에 사용할 시스템</param>
    /// <remarks>
    /// 코드리뷰 2026-07-06 H1 수정: 이전에는 <c>Tick</c>이 static <c>PlayerLevelSystem</c>을 통해
    /// 전역 static <c>LevelTable</c>에 직접 결합되어 있었다. 이제 생성자로 주입받아, 레벨 규칙이나
    /// 테스트용 데이터셋을 교체할 수 있다.
    /// </remarks>
    public BattleLoop(PlayerLevelSystem levelSystem)
    {
        ArgumentNullException.ThrowIfNull(levelSystem);
        _levelSystem = levelSystem;
    }

    /// <summary>
    /// player와 monster 사이의 라운드제 전투 교환 1회를 수행한다.
    /// </summary>
    /// <param name="player">공격을 시작하는 플레이어</param>
    /// <param name="monster">플레이어의 공격 대상이자 반격 주체인 몬스터</param>
    /// <param name="deltaTime">이번 틱에 해당하는 경과 시간(초) — 양쪽 <see cref="Entity.Update"/> 호출에 전달된다</param>
    /// <returns>이번 틱에 발생한 사망 이벤트</returns>
    /// <remarks>
    /// 순서: 양쪽 <see cref="Entity.Update"/>로 스탯 재계산·자연 회복·버프 틱 →
    /// 플레이어가 <see cref="BattleManager.CalcFinalDamage"/>로 공격 → 몬스터가 죽으면 보상 지급 후
    /// <see cref="PlayerLevelSystem.CheckLevelUp"/>로 레벨업 판정·적용 →
    /// <see cref="Entity.RestoreResources"/>로 즉시 재등장(이번 틱엔 몬스터의 반격이 없다) →
    /// 몬스터가 생존해 있으면 몬스터가 반격 → 플레이어가 죽으면 즉시 <see cref="Entity.RestoreResources"/>로 부활한다
    /// (부활 비용 차감은 아직 구현하지 않음 — 다음 사이클의 <c>ReviveCostCalculator</c> 대상).
    /// 단위 테스트에서 직접 호출할 수 있도록 <c>internal</c>로 노출한다(sleep·취소 없는 순수 로직).
    /// </remarks>
    internal BattleTickEvent Tick(Player player, Monster monster, float deltaTime)
    {
        player.Update(deltaTime);
        monster.Update(deltaTime);

        var damageToMonster = BattleManager.Instance.CalcFinalDamage(player, monster);
        monster.TakeDamage(damageToMonster);

        if (!monster.IsAlive)
        {
            var loot = monster.Rewards.GenerateLoot(1);
            player.AddExp(loot.TotalExp);
            player.AddGold(loot.TotalGold);
            _levelSystem.CheckLevelUp(player); // 경험치 획득 직후 레벨업 판정·적용
            monster.RestoreResources();
            return BattleTickEvent.MonsterDefeated;
        }

        var damageToPlayer = BattleManager.Instance.CalcFinalDamage(monster, player);
        player.TakeDamage(damageToPlayer);

        if (!player.IsAlive)
        {
            player.RestoreResources();
            return BattleTickEvent.PlayerDefeated;
        }

        return BattleTickEvent.None;
    }

    /// <summary>
    /// <see cref="Tick"/>을 반복 실행한다. <paramref name="cancellationToken"/>을 전달하지 않으면
    /// (기본값 <see cref="CancellationToken.None"/>) 취소될 수 없으므로 실질적으로 종료 조건이
    /// 없는 무한 루프가 된다.
    /// </summary>
    /// <param name="player">전투에 참여하는 플레이어</param>
    /// <param name="monster">전투에 참여하는 몬스터(사망 시 같은 인스턴스로 재등장)</param>
    /// <param name="tickInterval">틱 사이 대기 시간. 생략 시 500ms. <see cref="TimeSpan.Zero"/>면 대기 없이 즉시 다음 틱으로 진행(테스트용)</param>
    /// <param name="cancellationToken">루프를 중단시킬 토큰. 프로덕션 호출(예: <c>Main.cs</c>)은 이를 생략해 진짜 무한 루프로 동작시킨다</param>
    /// <param name="sink">이벤트를 기록할 싱크. 생략(null)하면 아무 것도 기록하지 않는다.</param>
    /// <returns>취소되기 전까지(또는 영구히) 완료되지 않는 <see cref="Task"/></returns>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Blocking 여부:</b> Non-blocking. <c>await Task.Delay</c>로 대기하는 동안
    /// 호출 스레드를 스레드 풀에 반환한다(코드리뷰 H2 — 이전 <c>Thread.Sleep</c> 버전은 대기 내내
    /// 스레드 하나를 점유해 다중 전투 동시 실행 시 스레드 기아를 유발했다).</description></item>
    /// <item><description><c>tickInterval</c> 대기는 취소 토큰을 즉시 관찰하지 않는다(이전 동기 버전과
    /// 동일한 특성 유지) — 취소 여부는 매 틱 시작 시점에만 확인하므로, 취소 후 최대 한 틱 간격만큼
    /// 늦게 종료될 수 있다.</description></item>
    /// </list>
    /// </remarks>
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
}
