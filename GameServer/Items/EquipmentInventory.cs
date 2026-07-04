using GameServer.Stats;

namespace GameServer.Items;

/// <summary>
/// 엔티티(주로 <see cref="Entities.Player"/>)가 착용 중인 무기·방어구·장신구를 슬롯별로 관리한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 장비 교체는 플레이어 액션 처리 스레드에서만 발생하는 것을 전제로 한다.</description></item>
/// </list>
/// </remarks>
public sealed class EquipmentInventory
{
    // 스켈레톤 단계: Equip/Unequip/GetAllModifiers 구현 시 실제로 읽고 쓰게 될 슬롯 필드.
    // 현재는 메서드 본문이 NotImplementedException만 던지므로 CS0169(미사용 필드)를 임시로 억제한다.
#pragma warning disable CS0169
    /// <summary>현재 착용 중인 무기 (미착용 시 null).</summary>
    private Weapon? _equippedWeapon;

    /// <summary>현재 착용 중인 방어구 (미착용 시 null).</summary>
    private Armor? _equippedArmor;

    /// <summary>현재 착용 중인 장신구 (미착용 시 null).</summary>
    private Accessory? _equippedAccessory;
#pragma warning restore CS0169

    /// <summary>지정한 슬롯에 장비를 착용시킨다. 기존 착용 장비는 교체된다.</summary>
    /// <param name="item">착용할 장비</param>
    /// <param name="slot">착용할 슬롯</param>
    public void Equip(Equipment item, SlotType slot) => throw new NotImplementedException();

    /// <summary>지정한 슬롯의 장비를 해제하고 반환한다.</summary>
    /// <param name="slot">해제할 슬롯</param>
    /// <returns>해제된 장비 (미착용 상태였다면 예외/null 처리는 구현 시 확정)</returns>
    public Equipment Unequip(SlotType slot) => throw new NotImplementedException();

    /// <summary>모든 착용 장비의 스탯 수정치를 합쳐 반환한다.</summary>
    /// <returns>착용 중인 전체 <see cref="StatModifier"/> 목록</returns>
    public List<StatModifier> GetAllModifiers() => throw new NotImplementedException();
}
