using GameServer.Entities;
using GameServer.Stats;

namespace GameServer.Systems;

/// <summary>
/// <see cref="MonsterTemplate"/>(마스터 데이터)로부터 실제 전투에 투입 가능한 <see cref="Monster"/> 인스턴스를 만든다.
/// </summary>
public static class MonsterFactory
{
    /// <summary>
    /// 템플릿 값으로 스탯·특성·보상·어픽스를 채운 <see cref="Monster"/>를 생성해 즉시 사용 가능한
    /// 상태로 반환한다.
    /// </summary>
    /// <param name="template">몬스터 마스터 데이터</param>
    /// <returns>스탯이 반영되고 풀피 상태로 생존 중인 새 몬스터 인스턴스</returns>
    /// <remarks>
    /// 호출마다 새 <see cref="Monster"/> 인스턴스를 만든다(같은 템플릿으로 여러 마리를 스폰해도
    /// 서로 독립적인 상태를 가짐). 내부에서 <see cref="Entity.UpdateFinalStats"/>와
    /// <see cref="Entity.RestoreResources"/>까지 호출해두므로, 호출 측이 별도로 스탯 갱신·풀피
    /// 세팅을 하지 않아도 곧바로 <see cref="BattleLoop"/> 등에 투입할 수 있다.
    /// </remarks>
    public static Monster CreateMonster(MonsterTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var monster = new Monster
        {
            InstanceId = $"{template.MonsterId}-{Guid.NewGuid():N}",
            MonsterId = template.MonsterId,
            Level = template.Level,
            BaseStats = new BaseStats
            {
                Hp = template.Hp,
                Atk = template.Atk,
                Def = template.Def,
                Recovery = template.Recovery
            },
            BaseTraits = new Traits
            {
                AtkSpeed = template.AtkSpeed,
                CritProb = template.CritProb,
                CritDmg = template.CritDmg
            },
            MonsterAffixes = new List<StatModifier>(template.Affixes),
            Rewards = new RewardComponent
            {
                ExpDrop = template.ExpDrop,
                GoldDrop = template.GoldDrop,
                DropTable = new List<DropPool>(template.DropTable)
            }
        };

        monster.UpdateFinalStats();
        monster.RestoreResources();

        return monster;
    }
}
