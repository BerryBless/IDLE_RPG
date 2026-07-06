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
    /// 코드리뷰 2026-07-06 Low 수정: <c>GameServer.Items.EquipmentFactory.Create</c>와 이름을 맞추기
    /// 위해 <c>CreateMonster</c>에서 <c>Create</c>로 이름을 바꿨다(둘 다 "템플릿→도메인 객체"
    /// 역할이면서 메서드명 관례가 어긋나 있었다).
    /// <b>Thread Safety:</b> Thread-safe. 정적 필드나 공유 상태가 없고, 매 호출이 완전히 독립적인
    /// 새 인스턴스를 생성·반환한다.
    /// <b>Memory Allocation:</b> 호출 1회당 <see cref="Monster"/>/<see cref="BaseStats"/>/<see cref="Traits"/>/
    /// <see cref="RewardComponent"/> 및 Affixes·DropTable 복사 리스트를 새로 할당한다(Zero-allocation
    /// 아님). <c>InstanceId</c>도 <see cref="Guid.NewGuid"/> + 문자열 보간으로 매번 새로 할당된다 —
    /// 현재는 몬스터가 <see cref="Entity.RestoreResources"/>로 재사용되고 킬마다 재생성되지 않아
    /// 문제되지 않지만, 향후 <c>MonsterSpawner</c>가 킬마다 새로 스폰하는 구조로 바뀌면 GC 압력
    /// 요인이 될 수 있다(다음 사이클 검토 대상, 이번 사이클에서는 손대지 않음).
    /// <b>Blocking 여부:</b> 즉시 반환(동기, non-blocking). I/O 없음.
    /// </remarks>
    public static Monster Create(MonsterTemplate template)
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
