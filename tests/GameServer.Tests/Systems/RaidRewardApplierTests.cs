using System.Collections.Concurrent;
using System.Threading.Channels;
using GameServer.Stats;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

/// <summary>
/// 코드리뷰 스타일 Medium 발견(<c>docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md</c>)
/// 수정: SRP 분리 전에는 보상 라우팅/적용 분기가 <c>SessionRaidRunner</c> 안에 묻혀 있어 세션 전체를
/// 구성해야 검증할 수 있었다. 분리 후 <see cref="RaidRewardApplier"/>만 독립적으로 구성해 라우팅
/// 성공/드롭/해제 후 라우팅 차단, 그리고 적용 로직을 직접 검증한다.
/// </summary>
public class RaidRewardApplierTests
{
    [Fact]
    public async Task DrainAsync_RoutesGrantToRegisteredQueue()
    {
        var applier = new RaidRewardApplier();
        var queue = new ConcurrentQueue<RaidRewardGrant>();
        applier.Register("player-1", queue);

        var channel = Channel.CreateUnbounded<RaidRewardGrant>();
        using var lifetimeCts = new CancellationTokenSource();
        var drainTask = applier.DrainAsync(channel.Reader, lifetimeCts.Token);

        channel.Writer.TryWrite(new RaidRewardGrant("player-1", 100, 50));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.True(queue.TryDequeue(out var grant));
        Assert.Equal(100, grant.Exp);
        Assert.Equal(50, grant.Gold);

        lifetimeCts.Cancel();
        await drainTask;
    }

    [Fact]
    public async Task DrainAsync_GrantForUnregisteredInstance_IsDroppedSilently()
    {
        // 등록된 적 없는 InstanceId(이미 접속 종료한 임시 플레이어를 흉내) — 예외 없이 조용히 드롭돼야 한다.
        var applier = new RaidRewardApplier();
        var channel = Channel.CreateUnbounded<RaidRewardGrant>();
        using var lifetimeCts = new CancellationTokenSource();
        var drainTask = applier.DrainAsync(channel.Reader, lifetimeCts.Token);

        channel.Writer.TryWrite(new RaidRewardGrant("ghost-player", 100, 50));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        lifetimeCts.Cancel();
        await drainTask; // 예외 없이 취소로 정상 종료하면 드롭이 조용히 처리됐다는 뜻
    }

    [Fact]
    public async Task Unregister_StopsRoutingFutureGrants()
    {
        var applier = new RaidRewardApplier();
        var queue = new ConcurrentQueue<RaidRewardGrant>();
        applier.Register("player-1", queue);
        applier.Unregister("player-1"); // 접속 해제 흉내

        var channel = Channel.CreateUnbounded<RaidRewardGrant>();
        using var lifetimeCts = new CancellationTokenSource();
        var drainTask = applier.DrainAsync(channel.Reader, lifetimeCts.Token);

        channel.Writer.TryWrite(new RaidRewardGrant("player-1", 100, 50));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.Empty(queue);

        lifetimeCts.Cancel();
        await drainTask;
    }

    [Fact]
    public void ApplyPending_DequeuesAllGrantsAndAppliesToPlayer()
    {
        var levelSystem = PlayerLevelSystem.CreateDefault();
        var player = PlayerFactory.CreateTemp(Guid.NewGuid(), levelSystem);
        var queue = new ConcurrentQueue<RaidRewardGrant>();
        queue.Enqueue(new RaidRewardGrant(player.InstanceId, 10, 5));
        queue.Enqueue(new RaidRewardGrant(player.InstanceId, 20, 15));

        var expBefore = player.CurrentExp;
        var goldBefore = player.CurrentGold;

        RaidRewardApplier.ApplyPending(player, queue, levelSystem);

        Assert.Empty(queue);
        Assert.Equal(expBefore + 30, player.CurrentExp);
        Assert.Equal(goldBefore + 20, player.CurrentGold);
    }
}
