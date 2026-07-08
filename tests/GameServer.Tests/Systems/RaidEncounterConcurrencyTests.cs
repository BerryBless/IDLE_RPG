using GameServer.Entities;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

/// <summary>
/// 공유 보스 co-op를 위해 <see cref="RaidEncounter"/>의 피해 채널을 <c>SingleWriter=false</c>로
/// 전환한 다중 생산자 경로를 실제 동시 제출로 검증한다. 기존 <see cref="RaidEncounterTests"/>는
/// 전부 단일 스레드에서 순차 제출하므로 이 다중 라이터 경로를 커버하지 않는다 — 사이클 1에서
/// "세션별 독립 몬스터"의 N=1→N=2 격리 갭을 메운 것과 동일한 이유의 필수 테스트다.
/// </summary>
public class RaidEncounterConcurrencyTests
{
    [Fact]
    public async Task RunAsync_ManyConcurrentWriters_AllDamageAppliedWithNoLossOrDoubleCount()
    {
        const int Writers = 8;
        const int HitsPerWriter = 500;
        const double DamagePerHit = 1;
        double bossHp = Writers * HitsPerWriter * DamagePerHit; // 정확히 소진되도록 설계

        var boss = RaidTestBoss.Make(hp: bossHp, def: 0, expDrop: 1000, goldDrop: 0);
        // timeLimit을 1시간으로 크게 잡아 동시 제출이 진행되는 동안 CheckDeadline이 우연히 만료되어
        // _contributions가 리셋되는 플레이키를 원천 차단한다(advisor 지적 — 실시간 clock 기본값은
        // 동시 부하 아래서 타이밍이 흔들릴 수 있음).
        var raid = new RaidEncounter(boss, TimeSpan.FromHours(1));
        var sink = new GameEventSink(TextWriter.Null);
        using var cts = new CancellationTokenSource();

        var runTask = raid.RunAsync(sink, cts.Token, onStep: (broadcast, _) =>
        {
            if (broadcast.Event == RaidEventType.BossDefeated)
            {
                cts.Cancel(); // 정확히 1회 처치되는 순간 액터를 정상 종료시킨다
            }
            return ValueTask.CompletedTask;
        });

        // Writers개의 독립 태스크가 서로 다른 플레이어 InstanceId로 동시에 SubmitDamage를 호출한다
        // — SingleWriter=false 전환이 실제로 다중 생산자를 안전하게 받아내는지가 이 테스트의 핵심.
        var writers = Enumerable.Range(0, Writers).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < HitsPerWriter; i++)
            {
                raid.SubmitDamage($"writer-{w}", DamagePerHit);
            }
        }));
        await Task.WhenAll(writers);

        // 액터가 4000건을 전부 소비하고 처치를 감지해 자가취소로 정상 종료할 때까지 대기한다.
        // 드레인은 반드시 이 await 이후에 해야 한다 — writer들의 제출 완료가 액터의 소비 완료를
        // 의미하지 않으므로, runTask 완료 전에 RewardReader를 읽으면 아직 안 써진 grant를 놓친다.
        await runTask;

        var grants = new List<RaidRewardGrant>();
        while (raid.RewardReader.TryRead(out var grant))
        {
            grants.Add(grant);
        }

        Assert.Equal(Writers, grants.Count); // 모든 라이터가 정확히 한 번씩 기여자로 집계됨(유실/중복 없음)
        Assert.Equal(1000, grants.Sum(g => g.Exp), precision: 5); // 전체 보상 풀이 손실 없이 분배됨
        Assert.Equal(boss.FinalStats.MaxHp, boss.FinalStats.CurrentHp); // 처치 후 즉시 재등장(풀피)
    }
}
