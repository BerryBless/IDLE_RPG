using GameServer.Stats;
using GameServer.Systems;

namespace GameServer.Entities;

/// <summary>스테이지에 배치되어 플레이어와 전투하는 몬스터 엔티티.</summary>
public sealed class Monster : Entity
{
    /// <summary>몬스터 정의 테이블(마스터 데이터)을 가리키는 ID.</summary>
    public int MonsterId { get; init; }

    /// <summary>이 몬스터를 처치했을 때 지급되는 보상 정의.</summary>
    public RewardComponent Rewards { get; init; } = new();

    /// <summary>
    /// 이 몬스터 개체에 고유하게 부여된 어픽스(특수 능력치) 목록.
    /// 몬스터 템플릿 데이터에서 개체 생성 시 주입되어야 하므로 public init으로 노출한다.
    /// </summary>
    public List<StatModifier> MonsterAffixes { get; init; } = new();

    /// <summary>몬스터 어픽스가 제공하는 스탯 수정치를 반환한다.</summary>
    /// <returns><see cref="MonsterAffixes"/> 목록</returns>
    protected override List<StatModifier> GetExtraModifiers() => MonsterAffixes;
}
