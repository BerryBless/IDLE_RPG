using GameServer.Stats;

namespace GameServer.Items;

/// <summary>
/// 장비 1종(種)의 마스터 데이터 정의(무기·방어구·장신구 공통, <see cref="Slot"/>으로 구분).
/// 로직 없는 순수 데이터 프로퍼티만 갖는다.
/// </summary>
/// <remarks>
/// 모든 프로퍼티가 기본 타입·단순 리스트(<see cref="StatModifier"/>도 로직 없는 순수 데이터
/// 클래스)로만 구성되어 있어 <c>System.Text.Json</c>으로 그대로 (역)직렬화할 수 있다. 현재는
/// <see cref="EquipmentTable"/>에 C# 하드코딩 리스트로 존재하지만(<see cref="GameServer.Systems.MonsterTable"/>과
/// 동일한 패턴), 나중에 JSON 파일 기반 마스터 데이터로 옮길 때 이 타입 자체는 바꿀 필요가 없다.
/// </remarks>
public sealed class EquipmentTemplate
{
    /// <summary>아이템 정의 테이블(마스터 데이터) ID. <see cref="Item.ItemMetaId"/>에 그대로 대응.</summary>
    public int ItemMetaId { get; init; }

    /// <summary>아이템 표시 이름.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>착용 슬롯 종류. <see cref="EquipmentFactory.Create"/>가 이 값으로 실제 구체 타입을 결정한다.</summary>
    public SlotType Slot { get; init; }

    /// <summary>공격 배율. <see cref="Slot"/>이 <see cref="SlotType.Weapon"/>일 때만 의미가 있고,
    /// 그 외 슬롯에서는 읽히지 않는 미사용 값(기본 0)이다.</summary>
    public float AttackScaling { get; init; }

    /// <summary>이 장비가 부여하는 기본 스탯 수정치. 강화/제작 시 랜덤으로 붙는
    /// <see cref="Equipment.RandomModifiers"/>는 인스턴스 생성 시점 개념이라 마스터 데이터에 포함하지 않는다.</summary>
    public List<StatModifier> BaseModifiers { get; init; } = new();
}
