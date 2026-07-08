using System.Collections.Concurrent;
using System.Threading.Channels;
using GameServer.Entities;

namespace GameServer.Systems;

/// <summary>
/// <see cref="RaidEncounter"/> 처치 보상을 <c>PlayerInstanceId</c>로 라우팅하고, 그 Player를 소유한
/// 스레드가 안전하게 적용할 수 있도록 하는 헬퍼. <see cref="SessionRaidRunner"/>의 SRP 위반(코드리뷰
/// Medium 발견, <c>docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md</c>) 수정으로
/// 분리됐다 — "라우팅(드레인 루프)"과 "적용(각 세션의 소유 스레드)"의 책임이 서로 다른 스레드에서
/// 실행돼야 하므로 하나의 메서드가 아니라 두 개의 진입점(<see cref="DrainAsync"/>/<see cref="ApplyPending"/>)으로
/// 나뉜다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>보상 단일 소유 원칙:</b> <see cref="DrainAsync"/>(드레인 루프, 인스턴스당
/// 1개)는 grant를 해당 세션의 <see cref="ConcurrentQueue{T}"/>에 enqueue만 할 뿐 <see cref="Player"/>를
/// 절대 직접 만지지 않는다. <see cref="Player.AddExp"/>/<see cref="Player.AddGold"/>/레벨업 판정은
/// 오직 그 Player를 소유한 스레드가 <see cref="ApplyPending"/>을 호출해서만 수행한다 — 서로 다른
/// 스레드가 동시에 같은 Player의 <c>UpdateFinalStats</c>를 재계산하는 레이스를 막기 위함이다.</description></item>
/// <item><description><b>Thread Safety:</b> <see cref="Register"/>/<see cref="Unregister"/>/
/// <see cref="DrainAsync"/>는 내부 <see cref="ConcurrentDictionary{TKey,TValue}"/>로 Thread-safe하다.
/// <see cref="ApplyPending"/>은 상태가 없는 정적 메서드이지만, 인자로 받는 <c>pendingRewards</c>
/// 큐는 호출자(그 Player를 소유한 세션의 제출 루프)만 <c>TryDequeue</c>해야 한다는 계약을
/// 전제한다(다중 소비자로 호출하면 단일 소유 원칙이 깨진다).</description></item>
/// </list>
/// </remarks>
public sealed class RaidRewardApplier
{
    // ConcurrentDictionary<string, ...>: 드레인 루프(생산자, 여러 세션의 라우팅 요청이 동시에 올 수
    // 있음)와 Register/Unregister(각 세션의 접속/해제 콜백, 역시 여러 I/O 스레드에서 동시 호출)가
    // 함께 접근하므로 락 없는 딕셔너리가 필수 — 사이클 1 SessionBattleRunner와 동일 근거.
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RaidRewardGrant>> _queuesByInstanceId = new();

    /// <summary>이 InstanceId로 도착하는 보상 grant를 라우팅할 대상 큐를 등록한다.</summary>
    /// <param name="instanceId">라우팅 키로 쓸 Player의 InstanceId</param>
    /// <param name="pendingRewards">그 세션의 제출 루프만 <c>TryDequeue</c>하는 큐(단일 소비자 계약)</param>
    public void Register(string instanceId, ConcurrentQueue<RaidRewardGrant> pendingRewards)
    {
        _queuesByInstanceId[instanceId] = pendingRewards;
    }

    /// <summary>세션 해제 시 라우팅 등록을 해제한다. 이후 도착하는 grant는 <see cref="DrainAsync"/>에서 드롭된다.</summary>
    /// <param name="instanceId"><see cref="Register"/>에 전달했던 것과 동일한 InstanceId</param>
    public void Unregister(string instanceId)
    {
        _queuesByInstanceId.TryRemove(instanceId, out _);
    }

    /// <summary>보스 처치로 발생한 보상 grant를 InstanceId로 등록된 큐에 라우팅하는 단일 드레인 루프.</summary>
    /// <param name="rewardReader"><see cref="RaidEncounter.RewardReader"/> — 처치 시 grant가 발행되는 채널 리더</param>
    /// <param name="lifetimeToken">서버 종료 시 취소되는 수명 토큰</param>
    /// <remarks>
    /// Player를 절대 직접 만지지 않는다(단일 소유 원칙, 클래스 remarks 참고) — 찾은 세션의
    /// <see cref="ConcurrentQueue{T}"/>에 enqueue만 한다. 등록되지 않은 InstanceId면(이미 접속
    /// 종료한 임시 플레이어) grant를 드롭한다 — 영속화가 없는 임시 Player라 무해한 no-op이다.
    /// <b>Blocking 여부:</b> Non-blocking. <c>ReadAllAsync</c>에서만 대기하며 호출 스레드를 점유하지 않는다.
    /// </remarks>
    public async Task DrainAsync(ChannelReader<RaidRewardGrant> rewardReader, CancellationToken lifetimeToken)
    {
        try
        {
            await foreach (var grant in rewardReader.ReadAllAsync(lifetimeToken))
            {
                if (_queuesByInstanceId.TryGetValue(grant.PlayerInstanceId, out var queue))
                {
                    queue.Enqueue(grant);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료(서버 수명 토큰 취소).
        }
    }

    /// <summary>대기 중인 보상 grant를 모두 꺼내 <paramref name="player"/>에게 적용한다.</summary>
    /// <param name="player">보상을 적용할 대상. 호출자가 이 Player를 소유한 유일한 스레드여야 한다(클래스 remarks 참고).</param>
    /// <param name="pendingRewards"><see cref="Register"/>로 등록해 둔, 호출자만 소비하는 큐</param>
    /// <param name="levelSystem">경험치 적용 후 레벨업 판정에 쓸 시스템</param>
    /// <remarks>
    /// <b>Thread Safety:</b> Not thread-safe로 설계됨 — <paramref name="player"/>를 소유한 단일
    /// 스레드(그 세션의 제출 루프)에서만 호출해야 한다. 상태가 없는 정적 메서드라 그 자체는
    /// 스레드 간 공유 상태를 갖지 않지만, 여러 스레드가 동시에 같은 Player로 호출하면 클래스
    /// remarks의 단일 소유 원칙이 깨진다.
    /// </remarks>
    public static void ApplyPending(Player player, ConcurrentQueue<RaidRewardGrant> pendingRewards, PlayerLevelSystem levelSystem)
    {
        while (pendingRewards.TryDequeue(out var grant))
        {
            player.AddExp(grant.Exp);
            player.AddGold(grant.Gold);
            levelSystem.CheckLevelUp(player);
        }
    }
}
