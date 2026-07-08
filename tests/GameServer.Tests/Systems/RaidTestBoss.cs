using GameServer.Entities;
using GameServer.Systems;

namespace GameServer.Tests.Systems;

/// <summary>
/// <see cref="RaidEncounterBroadcastTests"/>와 <see cref="RaidEncounterConcurrencyTests"/>가 각자
/// verbatim으로 들고 있던 <c>MakeBoss</c> 테스트 헬퍼 중복(코드리뷰 Low 발견,
/// <c>docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md</c>)을 이 클래스로 흡수한다.
/// </summary>
internal static class RaidTestBoss
{
    /// <summary>레이드 액터 테스트용 보스(반격 없음, Atk=0)를 생성한다.</summary>
    public static Monster Make(double hp, double def, double expDrop, double goldDrop)
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
}
