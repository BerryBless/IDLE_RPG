using GameServer.Stats;

namespace GameServer.Items;

/// <summary>
/// 착용 가능하여 스탯 수정치를 부여하는 아이템의 추상 기반 타입 (무기·방어구·장신구 공통).
/// </summary>
public abstract class Equipment : Item
{
    /// <summary>아이템 등급/템플릿에 의해 고정된 기본 수정치 목록.</summary>
    public List<StatModifier> BaseModifiers { get; init; } = new();

    /// <summary>아이템 생성(강화/제작) 시 랜덤으로 부여된 수정치 목록.</summary>
    public List<StatModifier> RandomModifiers { get; init; } = new();
}
