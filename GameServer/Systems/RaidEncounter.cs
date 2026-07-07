using System.Threading.Channels;
using GameServer.Entities;

namespace GameServer.Systems;

/// <summary>레이드 인코운터에서 한 스텝(피해 적용 또는 타이머 검사)에 발생한 이벤트 종류.</summary>
public enum RaidEventType
{
    /// <summary>이번 스텝에 특별한 이벤트가 없었다(보스 생존, 타이머 미만료).</summary>
    None,

    /// <summary>피해가 적용됐지만 보스가 아직 살아있다.</summary>
    BossDamaged,

    /// <summary>보스가 처치되어 기여 비례 보상을 분배하고 즉시 재등장했다.</summary>
    BossDefeated,

    /// <summary>제한시간이 만료되어 레이드가 실패했다(보상 없음, 상태 리셋).</summary>
    RaidFailed
}

/// <summary>샤드 스레드가 계산한 보스 피해 1건을 레이드 액터로 전달하는 메시지.</summary>
/// <remarks>
/// <b>Thread Safety:</b> 불변(readonly record struct)이라 스레드 간 값 복사로 전달되어 안전.
/// <b>Memory Allocation:</b> 값 타입이라 힙 할당 없음(Channel 내부 버퍼에 값으로 저장).
/// </remarks>
internal readonly record struct RaidAttackRequest(string PlayerInstanceId, BigNumber Damage);

/// <summary>보스 처치 시 기여 비례로 계산된 보상을, 그 플레이어를 소유한 샤드 스레드로 되돌려 보내는 메시지.</summary>
/// <remarks>
/// <b>Thread Safety:</b> 불변. Player 인스턴스 참조 대신 InstanceId만 담아, 액터가 Player 상태를
/// 직접 변경하지 않도록 한다 — Player 변경은 그 Player를 소유한 샤드 스레드만 수행한다는 단일
/// 소유 원칙을 유지한다.
/// </remarks>
public readonly record struct RaidRewardGrant(string PlayerInstanceId, BigNumber Exp, BigNumber Gold);

/// <summary>레이드 액터의 순수 판정 1스텝 결과. Channel/Thread I/O와 분리된 테스트 가능 경계.</summary>
/// <remarks><b>Memory Allocation:</b> 보스 생존/타이머 미만료 hot path는 <see cref="Array.Empty{T}"/>를
/// 반환해 무할당. 보스 처치·레이드 실패 시에만 리스트를 새로 할당한다.</remarks>
public readonly record struct RaidStepResult(RaidEventType Event, IReadOnlyList<RaidRewardGrant> Grants);

/// <summary>
/// 하나의 <see cref="Monster"/>(보스) HP를 여러 샤드 스레드가 동시에 깎는 공유 레이드 인코운터.
/// 샤드 스레드는 피해 "숫자"만 계산해 <see cref="SubmitDamage"/>로 보내고, 이 클래스의 단일 액터
/// 루프(<see cref="RunAsync"/>)가 보스 HP 변경·기여도 누적·사망/타임아웃 판정·기여 비례 보상
/// 분배·재등장을 전부 단일 스레드에서 순차 처리한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 보스의 <c>CurrentHp</c>/<c>CurrentMana</c> 및 기여도
/// Dictionary·데드라인은 <see cref="RunAsync"/> 액터 루프(단일 스레드) 안에서만 변경된다. 샤드
/// 스레드는 보스의 불변 필드(<c>FinalStats.Def</c>/<c>CombatTraits.ArmorPen</c>)만 읽어
/// <see cref="BattleManager.CalcFinalDamage"/>로 피해를 계산하고 그 숫자만 <see cref="SubmitDamage"/>로
/// 보낸다. 두 스레드가 만지는 보스 필드가 서로소이므로 락이 필요 없다.</description></item>
/// <item><description><b>⚠️ 불변식:</b> 생성 후 <c>boss.Update(...)</c>/<c>boss.UpdateFinalStats()</c>를
/// 절대(액터 스레드에서조차) 호출하지 않는다. 그 호출은 샤드가 동시에 읽는 <c>Def</c>/<c>Atk</c>/
/// <c>CombatTraits</c>를 재기록해 값이 같아도 데이터 레이스가 된다. 페이즈/버프가 없으므로 재계산
/// 자체가 불필요하다 — 액터의 유일한 보스 변경 연산은 <see cref="Entity.TakeDamage"/>와
/// <see cref="Entity.RestoreResources"/>뿐이다.</description></item>
/// <item><description><b>Blocking 여부:</b> <see cref="RunAsync"/>는 <c>ReadAllAsync</c>의 <c>await</c>
/// 지점에서만 대기하며 호출 스레드를 점유하지 않는다(non-blocking). <see cref="SubmitDamage"/>와
/// 순수 판정 코어(<see cref="ApplyDamage"/>/<see cref="CheckDeadline"/>)는 즉시 반환한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 보스 생존 hot path는 <c>Array.Empty</c> 반환으로
/// 무할당. 보스 처치 시에만 기여자 수만큼 <see cref="RaidRewardGrant"/> 리스트를 1회 할당한다.</description></item>
/// </list>
/// </remarks>
public sealed class RaidEncounter
{
    private readonly Monster _boss;
    private readonly TimeSpan _timeLimit;
    private readonly Func<DateTime> _clock;

    // Dictionary<string, BigNumber>: 액터 루프(단일 스레드) 안에서만 읽고 쓰므로 락 없는 일반
    // Dictionary로 충분하다 — 접근자가 정확히 하나뿐이라 ConcurrentDictionary가 필요 없다.
    private readonly Dictionary<string, BigNumber> _contributions = new();

    // Channel<RaidAttackRequest>: 샤드(생산자)→액터(소비자) 큐. fire-and-forget으로 매 틱 무조건
    // 피해를 보내야 하며 소비가 밀려도 전투 루프가 블로킹되면 안 되므로 Unbounded. 현재 정확히
    // 한 샤드만 레이드에 참여하므로 SingleWriter=true — 여러 샤드가 참여하는 레이드로 확장하면
    // SingleWriter=false로 바꿔야 한다(다중 생산자가 되므로).
    private readonly Channel<RaidAttackRequest> _damageChannel;

    // Channel<RaidRewardGrant>: 액터(생산자)→샤드(소비자) 역방향 큐. 보상 지급(Player.AddExp/AddGold)은
    // 그 Player를 소유한 샤드 스레드만 수행해야 하므로(단일 소유 원칙), 액터는 수치만 담은 grant를
    // 여기 써서 넘기고 Player 인스턴스를 직접 만지지 않는다.
    private readonly Channel<RaidRewardGrant> _rewardChannel;

    private DateTime _deadlineUtc;

    /// <summary>레이드 인코운터를 생성한다.</summary>
    /// <param name="boss">공유 보스. <see cref="MonsterFactory.Create"/> 등으로 스폰 완료(FinalStats
    /// 계산·풀피) 상태여야 하며, 생성 후에는 이 인스턴스에 Update/UpdateFinalStats를 호출하지 말 것.</param>
    /// <param name="timeLimit">이 시간 내에 처치하지 못하면 레이드 실패(HP·기여도 리셋, 보상 없음).</param>
    /// <param name="clock">현재 시각 공급자. 테스트에서 결정적 타이머 제어를 위해 주입(기본 <see cref="DateTime.UtcNow"/>).</param>
    /// <exception cref="ArgumentNullException"><paramref name="boss"/>가 null인 경우</exception>
    public RaidEncounter(Monster boss, TimeSpan timeLimit, Func<DateTime>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(boss);
        _boss = boss;
        _timeLimit = timeLimit;
        _clock = clock ?? (() => DateTime.UtcNow);
        _damageChannel = Channel.CreateUnbounded<RaidAttackRequest>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _rewardChannel = Channel.CreateUnbounded<RaidRewardGrant>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _deadlineUtc = _clock() + _timeLimit;
    }

    /// <summary>샤드 스레드가 보스의 불변 스탯을 읽어 계산한 피해 숫자를 액터에게 보낸다.</summary>
    /// <param name="playerInstanceId">피해를 입힌 플레이어의 InstanceId(기여도 추적용)</param>
    /// <param name="damage">계산된 피해량</param>
    /// <remarks>
    /// <b>Thread Safety:</b> 현재 스코프에서는 단일 샤드(단일 생산자)만 호출한다 — 생성자에서
    /// <c>_damageChannel</c>을 <c>SingleWriter=true</c>로 구성했으므로, 여러 샤드가 동시에 이
    /// 메서드를 호출하는 다중 생산자로 확장하려면 그 설정도 함께 <c>SingleWriter=false</c>로
    /// 바꿔야 한다. <b>Blocking 여부:</b> Non-blocking — 무경계 채널의 TryWrite는 항상 즉시 성공한다.
    /// </remarks>
    public void SubmitDamage(string playerInstanceId, BigNumber damage)
        => _damageChannel.Writer.TryWrite(new RaidAttackRequest(playerInstanceId, damage));

    /// <summary>보스 처치로 발생한 보상 grant를 소유 샤드 스레드가 꺼내가기 위한 리더.</summary>
    public ChannelReader<RaidRewardGrant> RewardReader => _rewardChannel.Reader;

    /// <summary>피해 1건을 보스에 적용하고 기여도를 누적한다. 보스가 죽으면 보상을 분배하고 즉시 재등장시킨다.</summary>
    /// <param name="request">적용할 피해 요청</param>
    /// <returns>이번 적용의 결과(생존 시 <see cref="RaidEventType.BossDamaged"/>, 처치 시
    /// <see cref="RaidEventType.BossDefeated"/> + 보상 grant 목록)</returns>
    /// <remarks>
    /// 단위 테스트에서 Channel/Thread 없이 직접 호출할 수 있도록 <c>internal</c>로 노출한다(기존
    /// <see cref="BattleLoop.Tick"/>과 동일한 노출 방식). 액터 루프(<see cref="RunAsync"/>) 전용 —
    /// 다른 스레드에서 호출하면 안 된다.
    /// </remarks>
    internal RaidStepResult ApplyDamage(RaidAttackRequest request)
    {
        _boss.TakeDamage(request.Damage);
        // 기여도는 실제 HP 차감분과 일관되게 음수를 0으로 클램프한다 — TakeDamage 내부에서도
        // 동일하게 클램프하지만(Entity.cs), 여기서 별도로 기록하는 값이라 방어적으로 한 번 더 막는다.
        var clampedDamage = Math.Max(0, request.Damage);
        _contributions[request.PlayerInstanceId] =
            _contributions.GetValueOrDefault(request.PlayerInstanceId) + clampedDamage;

        if (_boss.IsAlive)
        {
            return new RaidStepResult(RaidEventType.BossDamaged, Array.Empty<RaidRewardGrant>());
        }

        var grants = DistributeRewards();
        _boss.RestoreResources();
        _contributions.Clear();
        _deadlineUtc = _clock() + _timeLimit;
        return new RaidStepResult(RaidEventType.BossDefeated, grants);
    }

    /// <summary>제한시간 만료를 검사한다. 만료면 보상 없이 보스 HP·기여도를 리셋하고 타이머를 재시작한다.</summary>
    /// <param name="nowUtc">현재 시각(UTC)</param>
    /// <remarks>액터 루프 전용 — <see cref="ApplyDamage"/>와 동일한 접근 제약.</remarks>
    internal RaidStepResult CheckDeadline(DateTime nowUtc)
    {
        if (nowUtc < _deadlineUtc)
        {
            return new RaidStepResult(RaidEventType.None, Array.Empty<RaidRewardGrant>());
        }

        _boss.RestoreResources();
        _contributions.Clear();
        _deadlineUtc = nowUtc + _timeLimit;
        return new RaidStepResult(RaidEventType.RaidFailed, Array.Empty<RaidRewardGrant>());
    }

    /// <summary>현재까지의 기여도를 비율로 환산해 보스 보상 풀을 나눈다. 총 기여가 0이면 빈 목록(0나눗셈 방지).</summary>
    private List<RaidRewardGrant> DistributeRewards()
    {
        BigNumber total = 0;
        foreach (var damage in _contributions.Values)
        {
            total += damage;
        }

        var grants = new List<RaidRewardGrant>(_contributions.Count);
        if (total <= 0)
        {
            return grants;
        }

        // 액터 단일 스레드에서만 호출 → RewardComponent 내부 비스레드안전 Random이 안전하게 소비됨
        var pool = _boss.Rewards.GenerateLoot(1);
        foreach (var (playerInstanceId, damage) in _contributions)
        {
            var ratio = damage / total;
            grants.Add(new RaidRewardGrant(playerInstanceId, pool.TotalExp * ratio, pool.TotalGold * ratio));
        }
        return grants;
    }

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
}
