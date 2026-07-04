namespace GameServer.Items;

/// <summary>
/// <see cref="EquipmentInventory"/>가 관리하는 장비 슬롯의 종류.
/// 다이어그램에는 참조만 되고 정의되지 않아 <see cref="Equipment"/> 하위 타입에 1:1 대응하도록 신규 정의했다.
/// </summary>
public enum SlotType
{
    /// <summary>무기 슬롯.</summary>
    Weapon,

    /// <summary>방어구 슬롯.</summary>
    Armor,

    /// <summary>장신구 슬롯.</summary>
    Accessory
}
