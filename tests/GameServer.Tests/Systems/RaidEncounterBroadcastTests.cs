using GameServer.Entities;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

/// <summary>
/// <see cref="RaidEncounter.RunAsync"/>에 추가된 선택적 <c>onStep</c> 콜백(공유 보스 co-op의 네트워크
/// 브로드캐스트 훅)이 매 스텝마다 올바른 <see cref="RaidStepBroadcast"/>를 넘기는지 검증한다.
/// <see cref="RaidEncounterTests"/>의 순수 판정 코어(ApplyDamage/CheckDeadline) 테스트와는 별도로,
/// 이 파일은 액터 루프(RunAsync) + 콜백 배선만 다룬다.
/// </summary>
public class RaidEncounterBroadcastTests
{
    private static Monster MakeBoss(double hp, double def, double expDrop, double goldDrop)
    {
        var boss = new Monster
        {
            InstanceId = "raid-boss",
            MonsterId = 7001,
            Level = 20,
            Rewards = new RewardComponent { ExpDrop = expDrop, GoldDrop = goldDrop }
        };
        boss.BaseStats.Hp = hp;
        boss.BaseStats.Def = def;
        boss.BaseStats.Atk = 0;
        boss.UpdateFinalStats();
        boss.RestoreResources();
        return boss;
    }

    [Fact]
    public async Task RunAsync_OnBossDefeated_OnStepReceivesMvpTopDamageAndGenerationTransition()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var boss = MakeBoss(hp: 100, def: 0, expDrop: 1000, goldDrop: 2000);
        // timeLimit을 1시간으로 크게 잡아 테스트 실행 중 CheckDeadline이 우연히 만료되지 않게 한다
        // (advisor 지적 — 실시간 clock을 쓰면 느린 CI에서 플레이키해질 수 있음).
        var raid = new RaidEncounter(boss, TimeSpan.FromHours(1), () => now);
        var sink = new GameEventSink(TextWriter.Null);
        using var cts = new CancellationTokenSource();

        var broadcasts = new List<RaidStepBroadcast>();

        raid.SubmitDamage("p1", 30);
        raid.SubmitDamage("p2", 70); // 정확히 처치(100 HP) — p2가 더 많이 기여해 MVP가 되어야 함

        await raid.RunAsync(sink, cts.Token, onStep: (broadcast, _) =>
        {
            broadcasts.Add(broadcast);
            if (broadcast.Event == RaidEventType.BossDefeated)
            {
                cts.Cancel(); // 처치 확인 즉시 취소 — 그 외엔 무한 대기이므로 테스트가 끝나지 않음
            }
            return ValueTask.CompletedTask;
        });

        var defeated = Assert.Single(broadcasts, b => b.Event == RaidEventType.BossDefeated);
        Assert.Equal("p2", defeated.MvpName);
        Assert.Equal(70, defeated.TopDamage);
        Assert.Equal(1, defeated.DeadGeneration);
        Assert.Equal(2, defeated.NewGeneration);
        Assert.Equal((long)boss.FinalStats.MaxHp, defeated.CurrentHp); // 재등장 풀피가 이미 반영됨
        Assert.Equal((long)boss.FinalStats.MaxHp, defeated.MaxHp);
    }

    [Fact]
    public async Task RunAsync_OnBossDamaged_OnStepReceivesNoneMvpAndUnchangedGeneration()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var boss = MakeBoss(hp: 100, def: 0, expDrop: 1000, goldDrop: 2000);
        var raid = new RaidEncounter(boss, TimeSpan.FromHours(1), () => now);
        var sink = new GameEventSink(TextWriter.Null);
        using var cts = new CancellationTokenSource();

        var broadcasts = new List<RaidStepBroadcast>();
        raid.SubmitDamage("p1", 40); // 생존(60/100)

        await raid.RunAsync(sink, cts.Token, onStep: (broadcast, _) =>
        {
            broadcasts.Add(broadcast);
            cts.Cancel(); // 첫 스텝(BossDamaged) 확인 즉시 취소
            return ValueTask.CompletedTask;
        });

        var damaged = Assert.Single(broadcasts, b => b.Event == RaidEventType.BossDamaged);
        Assert.Equal(string.Empty, damaged.MvpName);
        Assert.Equal(0, damaged.TopDamage);
        Assert.Equal(1, damaged.DeadGeneration);
        Assert.Equal(1, damaged.NewGeneration); // 처치가 아니므로 세대 불변
        Assert.Equal(60, damaged.CurrentHp);
    }

    [Fact]
    public async Task RunAsync_OnRaidFailed_OnStepReceivesGenerationBumpWithoutMvp()
    {
        // 판정 로직 자체(보상 없음·HP 리셋)는 RaidEncounterTests.CheckDeadline_AfterDeadline_...가
        // 이미 검증했다 — 여기서는 RunAsync가 그 RaidFailed 스텝을 onStep으로 올바르게 전달하는지
        // (세대 증가·MVP 없음)만 확인한다. 실제 시간 경과로 데드라인을 만료시키기 위해 timeLimit을
        // 아주 짧게(30ms) 잡고, RunAsync 시작 전 최소 1건을 미리 제출해 루프가 최소 1회 순회하도록
        // 한다(제출이 없으면 ReadAllAsync가 항목을 기다리며 대기해 CheckDeadline이 전혀 실행되지 않음).
        var boss = MakeBoss(hp: 100, def: 0, expDrop: 1000, goldDrop: 2000);
        var raid = new RaidEncounter(boss, TimeSpan.FromMilliseconds(30));
        var sink = new GameEventSink(TextWriter.Null);
        using var cts = new CancellationTokenSource();

        var broadcasts = new List<RaidStepBroadcast>();
        raid.SubmitDamage("p1", 40); // 생존 피해 — 데드라인 만료 전이므로 이 스텝 자체는 BossDamaged
        await Task.Delay(50); // 데드라인(30ms) 경과를 보장

        var runTask = raid.RunAsync(sink, cts.Token, onStep: (broadcast, _) =>
        {
            broadcasts.Add(broadcast);
            if (broadcast.Event == RaidEventType.RaidFailed)
            {
                cts.Cancel();
            }
            return ValueTask.CompletedTask;
        });
        await runTask;

        var failed = Assert.Single(broadcasts, b => b.Event == RaidEventType.RaidFailed);
        Assert.Equal(string.Empty, failed.MvpName);
        Assert.Equal(0, failed.TopDamage);
        Assert.Equal(1, failed.DeadGeneration);
        Assert.Equal(2, failed.NewGeneration); // 실패도 보스를 리셋하므로 새 세대로 취급
        Assert.Equal((long)boss.FinalStats.MaxHp, failed.CurrentHp);
    }
}
