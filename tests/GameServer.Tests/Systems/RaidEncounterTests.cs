using System.Threading.Channels;
using GameServer.Entities;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class RaidEncounterTests
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
    public void ApplyDamage_BossSurvives_ReturnsBossDamagedWithNoGrants()
    {
        var boss = MakeBoss(hp: 100, def: 0, expDrop: 1000, goldDrop: 2000);
        var raid = new RaidEncounter(boss, TimeSpan.FromSeconds(30));

        var result = raid.ApplyDamage(new RaidAttackRequest("p1", 40));

        Assert.Equal(RaidEventType.BossDamaged, result.Event);
        Assert.Empty(result.Grants);
        Assert.Equal(60, boss.FinalStats.CurrentHp);
    }

    [Fact]
    public void ApplyDamage_BossDefeated_DistributesRewardsByContributionRatio()
    {
        // 보스 HP=100, ExpDrop=1000/GoldDrop=2000. p1이 40, p2가 60을 입혀 정확히 처치 —
        // 기여 비율 40%:60%대로 보상이 나뉘어야 한다.
        var boss = MakeBoss(hp: 100, def: 0, expDrop: 1000, goldDrop: 2000);
        var raid = new RaidEncounter(boss, TimeSpan.FromSeconds(30));
        raid.ApplyDamage(new RaidAttackRequest("p1", 40));

        var result = raid.ApplyDamage(new RaidAttackRequest("p2", 60));

        Assert.Equal(RaidEventType.BossDefeated, result.Event);
        Assert.Equal(2, result.Grants.Count);
        var p1Grant = result.Grants.Single(g => g.PlayerInstanceId == "p1");
        var p2Grant = result.Grants.Single(g => g.PlayerInstanceId == "p2");
        Assert.Equal(400, p1Grant.Exp, precision: 5);
        Assert.Equal(800, p1Grant.Gold, precision: 5);
        Assert.Equal(600, p2Grant.Exp, precision: 5);
        Assert.Equal(1200, p2Grant.Gold, precision: 5);
        Assert.Equal(boss.FinalStats.MaxHp, boss.FinalStats.CurrentHp); // 즉시 재등장(풀피)
    }

    [Fact]
    public void ApplyDamage_AfterPreviousKill_ContributionsWereCleared()
    {
        // 처치 1회차: p1=40, p2=60. 처치 2회차: p1 혼자 100을 입혀 처치 — 기여도가 초기화됐다면
        // 이번엔 p1 혼자 100% 보상을 받아야 한다(이전 40이 남아있으면 비율이 달라짐).
        var boss = MakeBoss(hp: 100, def: 0, expDrop: 1000, goldDrop: 2000);
        var raid = new RaidEncounter(boss, TimeSpan.FromSeconds(30));
        raid.ApplyDamage(new RaidAttackRequest("p1", 40));
        raid.ApplyDamage(new RaidAttackRequest("p2", 60)); // 1차 처치, 기여도 Clear

        var result = raid.ApplyDamage(new RaidAttackRequest("p1", 100)); // 2차 처치, p1 단독

        Assert.Equal(RaidEventType.BossDefeated, result.Event);
        var onlyGrant = Assert.Single(result.Grants);
        Assert.Equal("p1", onlyGrant.PlayerInstanceId);
        Assert.Equal(1000, onlyGrant.Exp, precision: 5); // 100% 몫 — 이전 기여가 섞이지 않음
    }

    [Fact]
    public void CheckDeadline_BeforeDeadline_ReturnsNone()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var boss = MakeBoss(hp: 100, def: 0, expDrop: 10, goldDrop: 20);
        var raid = new RaidEncounter(boss, TimeSpan.FromSeconds(30), () => now);

        var result = raid.CheckDeadline(now.AddSeconds(10));

        Assert.Equal(RaidEventType.None, result.Event);
        Assert.Empty(result.Grants);
    }

    [Fact]
    public void CheckDeadline_AfterDeadline_FailsAndResetsWithoutRewards()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var boss = MakeBoss(hp: 100, def: 0, expDrop: 10, goldDrop: 20);
        var raid = new RaidEncounter(boss, TimeSpan.FromSeconds(30), () => now);
        raid.ApplyDamage(new RaidAttackRequest("p1", 50)); // 처치는 아님, 보스 HP=50

        var result = raid.CheckDeadline(now.AddSeconds(31));

        Assert.Equal(RaidEventType.RaidFailed, result.Event);
        Assert.Empty(result.Grants);
        Assert.Equal(boss.FinalStats.MaxHp, boss.FinalStats.CurrentHp); // 보상 없이 HP만 리셋
    }

    [Fact]
    public void ApplyDamage_KillResetsDeadline_SoImmediateCheckDeadlineReturnsNone()
    {
        // 처치 스텝 자체가 데드라인을 재시작하므로, 처치 직후 같은 시각으로 CheckDeadline을 불러도
        // 실패로 뒤집히면 안 된다.
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var boss = MakeBoss(hp: 10, def: 0, expDrop: 10, goldDrop: 20);
        var raid = new RaidEncounter(boss, TimeSpan.FromSeconds(30), () => now);

        raid.ApplyDamage(new RaidAttackRequest("p1", 10)); // 처치 → 데드라인 now+30s로 재시작
        var result = raid.CheckDeadline(now);

        Assert.Equal(RaidEventType.None, result.Event);
    }

    [Fact]
    public void ApplyDamage_TotalContributionZero_ReturnsEmptyGrantsWithoutDivideByZero()
    {
        // 방어적 경계 케이스: 보스 HP가 이미 0인 상태에서 0데미지 요청이 들어오면 총 기여가 0이라
        // 나눗셈 없이 빈 목록을 반환해야 한다.
        var boss = MakeBoss(hp: 0, def: 0, expDrop: 10, goldDrop: 20);
        var raid = new RaidEncounter(boss, TimeSpan.FromSeconds(30));

        var result = raid.ApplyDamage(new RaidAttackRequest("p1", 0));

        Assert.Equal(RaidEventType.BossDefeated, result.Event);
        Assert.Empty(result.Grants);
    }

    [Fact]
    public async Task RunAsync_WithCancellationToken_StopsWithoutHanging()
    {
        var boss = MakeBoss(hp: 1_000_000, def: 0, expDrop: 1, goldDrop: 1);
        var raid = new RaidEncounter(boss, TimeSpan.FromSeconds(30));
        var logChannel = Channel.CreateUnbounded<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        raid.SubmitDamage("p1", 100); // 취소 전에 채널에 미리 넣어 ReadAllAsync가 최소 1회 순회하게 함

        await raid.RunAsync(logChannel.Writer, cts.Token);

        // 취소 후 정상 반환된 것 자체가 핵심 검증(무한 대기에 갇히지 않음, 스레드도 점유하지 않음).
        // 제출한 데미지가 실제로 적용됐는지도 함께 확인.
        Assert.Equal(999_900, boss.FinalStats.CurrentHp);
    }
}
