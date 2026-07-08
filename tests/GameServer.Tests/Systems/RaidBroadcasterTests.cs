using System.Net;
using GameServer.Systems;
using ServerLib.Interface;

namespace GameServer.Tests.Systems;

/// <summary>
/// 코드리뷰 스타일 Medium 발견(<c>docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md</c>)
/// 수정: SRP 분리 전에는 <see cref="RaidBroadcaster"/>의 스로틀/전송 분기가 <c>SessionRaidRunner</c>
/// 안에 묻혀 있어 <see cref="ISession"/> 전체를 흉내 내지 않고는 단위 테스트가 어려웠다. 분리 후
/// 이 클래스만 독립적으로 구성해 스로틀 경계 분기를 직접 검증한다.
/// </summary>
public class RaidBroadcasterTests
{
    /// <summary><see cref="ISessionRegistry.BroadcastAsync"/> 호출 횟수만 기록하는 가짜 레지스트리.</summary>
    private sealed class RecordingSessionRegistry : ISessionRegistry
    {
        private int _broadcastCallCount;
        public int BroadcastCallCount => Volatile.Read(ref _broadcastCallCount);

        public int Count => 0;
        public bool TryGet(Guid sessionId, out ISession? session) { session = null; return false; }
        public IReadOnlyCollection<ISession> GetAll() => Array.Empty<ISession>();

        public ValueTask BroadcastAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _broadcastCallCount);
            return ValueTask.CompletedTask;
        }
    }

    private static RaidStepBroadcast HpOnlyStep(long hp) =>
        new(RaidEventType.BossDamaged, hp, MaxHp: 1000, DeadGeneration: 1, NewGeneration: 1, MvpName: string.Empty, TopDamage: 0);

    private static RaidStepBroadcast DefeatedStep() =>
        new(RaidEventType.BossDefeated, CurrentHp: 0, MaxHp: 1000, DeadGeneration: 1, NewGeneration: 2, MvpName: "p1", TopDamage: 999);

    [Fact]
    public async Task RapidHpOnlySteps_AreThrottled_ToOneBroadcastPerWindow()
    {
        var registry = new RecordingSessionRegistry();
        var broadcaster = new RaidBroadcaster(registry, hpBroadcastThrottle: TimeSpan.FromMilliseconds(80));
        using var lifetimeCts = new CancellationTokenSource();
        var drainTask = broadcaster.DrainAsync(lifetimeCts.Token);

        // 스로틀 윈도우 안에서 3번 연속 제출 — HP 전용 스텝(BossDamaged)은 첫 번째만 통과해야 한다.
        await broadcaster.OnStepAsync(HpOnlyStep(90), CancellationToken.None);
        await broadcaster.OnStepAsync(HpOnlyStep(80), CancellationToken.None);
        await broadcaster.OnStepAsync(HpOnlyStep(70), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100)); // 드레인 루프가 3건 모두 소비할 시간

        Assert.Equal(1, registry.BroadcastCallCount);

        // 스로틀 윈도우가 지난 뒤 제출하면 다시 통과해야 한다.
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await broadcaster.OnStepAsync(HpOnlyStep(60), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.Equal(2, registry.BroadcastCallCount);

        lifetimeCts.Cancel();
        await drainTask;
    }

    [Fact]
    public async Task BossDefeatedSteps_AreNeverThrottled_AndSendDeathPlusHpPackets()
    {
        var registry = new RecordingSessionRegistry();
        // 스로틀을 크게 잡아 "스로틀과 무관하게 항상 전송"을 명확히 구분한다.
        var broadcaster = new RaidBroadcaster(registry, hpBroadcastThrottle: TimeSpan.FromSeconds(10));
        using var lifetimeCts = new CancellationTokenSource();
        var drainTask = broadcaster.DrainAsync(lifetimeCts.Token);

        await broadcaster.OnStepAsync(DefeatedStep(), CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        // BossDefeated는 Death + Hp 패킷 2건이 각각 1회 BroadcastAsync를 호출한다(RaidBroadcastPackets.Build 계약).
        Assert.Equal(2, registry.BroadcastCallCount);

        lifetimeCts.Cancel();
        await drainTask;
    }
}
