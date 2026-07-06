using GameServer.Stats;

namespace GameServer.Items;

/// <summary>
/// <see cref="EquipmentTemplate"/>(마스터 데이터)로부터 실제 장착 가능한 <see cref="Equipment"/>
/// 구체 인스턴스(<see cref="Weapon"/>/<see cref="Armor"/>/<see cref="Accessory"/>)를 만든다.
/// </summary>
public static class EquipmentFactory
{
    /// <summary>
    /// 템플릿의 <see cref="EquipmentTemplate.Slot"/> 값에 따라 알맞은 구체 타입의 장비를 생성한다.
    /// </summary>
    /// <param name="template">장비 마스터 데이터</param>
    /// <returns>템플릿 값이 반영된 새 장비 인스턴스</returns>
    /// <remarks>
    /// 호출마다 새 인스턴스를 만들며(같은 템플릿으로 여러 개를 생성해도 서로 독립), <see cref="Equipment.BaseModifiers"/>는
    /// 템플릿의 리스트를 복사해 전달한다 — 여러 인스턴스가 같은 리스트 참조를 공유해 한쪽 변경이
    /// 다른 쪽에 영향을 주는 것을 방지한다. <see cref="Equipment.RandomModifiers"/>는 마스터 데이터
    /// 개념이 아니므로 비운 채로 반환한다(강화/제작 시스템 도입 시 별도로 채워질 값).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="template"/>이 null인 경우</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="EquipmentTemplate.Slot"/>이 알 수 없는 값인 경우</exception>
    public static Equipment Create(EquipmentTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        var instanceId = $"{template.ItemMetaId}-{Guid.NewGuid():N}";
        var baseModifiers = new List<StatModifier>(template.BaseModifiers);

        return template.Slot switch
        {
            SlotType.Weapon => new Weapon
            {
                InstanceId = instanceId,
                ItemMetaId = template.ItemMetaId,
                Name = template.Name,
                AttackScaling = template.AttackScaling,
                BaseModifiers = baseModifiers
            },
            SlotType.Armor => new Armor
            {
                InstanceId = instanceId,
                ItemMetaId = template.ItemMetaId,
                Name = template.Name,
                BaseModifiers = baseModifiers
            },
            SlotType.Accessory => new Accessory
            {
                InstanceId = instanceId,
                ItemMetaId = template.ItemMetaId,
                Name = template.Name,
                BaseModifiers = baseModifiers
            },
            _ => throw new ArgumentOutOfRangeException(nameof(template), template.Slot, "지원하지 않는 SlotType입니다.")
        };
    }
}
