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

    /// <summary>현재 착용 중인 장신구
    /// (미착용 시 null).</summary>
    private Accessory? _equippedAccessory;
    
    // 장비가 변경되었는지 체크하는 플래그
    private bool _isDirty = true;
    // 캐싱된 스탯 모디파이어 리스트 (매번 새로 만들지 않음!)
    private readonly List<StatModifier> _cachedModifiers = new List<StatModifier>();
    
#pragma warning restore CS0169

    public Weapon? GetWeapon()
    {
        return _equippedWeapon;
    }
    
    
    /// <summary>지정한 슬롯에 장비를 착용시킨다. 기존 착용 장비는 교체된다.</summary>
    /// <param name="item">착용할 장비</param>
    /// <param name="slot">착용할 슬롯</param>
    public void Equip(Equipment item, SlotType slot)
    {
        switch (slot)
        {
            case SlotType.Weapon:
                if (item is Weapon weapon) _equippedWeapon = weapon;
                else throw new ArgumentException("해당 슬롯에는 무기만 장착할 수 있습니다.", nameof(item));
                break;
            
            case SlotType.Armor:
                if (item is Armor armor) _equippedArmor = armor;
                else throw new ArgumentException("해당 슬롯에는 방어구만 장착할 수 있습니다.", nameof(item));
                break;
                
            case SlotType.Accessory:
                if (item is Accessory accessory) _equippedAccessory = accessory;
                else throw new ArgumentException("해당 슬롯에는 장신구만 장착할 수 있습니다.", nameof(item));
                break; 
                
            default:
                throw new ArgumentOutOfRangeException(nameof(slot), slot, null);
        }
        _isDirty = true;
    }

    /// <summary>지정한 슬롯의 장비를 해제하고 반환한다.</summary>
    /// <param name="slot">해제할 슬롯</param>
    /// <returns>해제된 장비 (미착용 상태였다면 예외/null 처리는 구현 시 확정)</returns>
    public Equipment? Unequip(SlotType slot)
    {
        Equipment? unequippedItem = null;

        switch (slot)
        {
            case SlotType.Armor:
                unequippedItem = _equippedArmor;
                _equippedArmor = null;
                break;
                
            case SlotType.Accessory:
                unequippedItem = _equippedAccessory;
                _equippedAccessory = null;
                break;
                
            case SlotType.Weapon:
                unequippedItem = _equippedWeapon;
                _equippedWeapon = null;
                break;
                
            default:
                throw new ArgumentOutOfRangeException(nameof(slot), slot, null);
        }
        
        _isDirty = true;
        return unequippedItem;
    }

    /// <summary>모든 착용 장비의 스탯 수정치를 합쳐 반환한다.</summary>
    /// <returns>착용 중인 전체 <see cref="StatModifier"/> 목록</returns>
    public IReadOnlyList<StatModifier> GetAllModifiers()
    {
        // 장비 변경사항이 없다면, 미리 만들어둔 리스트를 그대로 반환 (메모리 할당 0)
        if (!_isDirty) 
        {
            return _cachedModifiers;
        }

        // 장비가 변경되었다면 캐시를 비우고 다시 채움
        _cachedModifiers.Clear();
        // 1. 우선 장착 중인 모든 수정치를 임시로 모읍니다.
        var tempModifiers = new List<StatModifier>();
        if (_equippedWeapon != null)
        {
            tempModifiers.AddRange(_equippedWeapon.Modifiers);
        }
        if (_equippedArmor != null) tempModifiers.AddRange(_equippedArmor.Modifiers);
        if (_equippedAccessory != null) tempModifiers.AddRange(_equippedAccessory.Modifiers);

        // 2. StatType과 ModType이 동일한 것끼리 그룹화(GroupBy)하여 Value를 합산(Sum)합니다.
        var mergedModifiers = tempModifiers
            .GroupBy(m => new { m.StatType, m.ModType })
            .Select(g => new StatModifier
            {
                StatType = g.Key.StatType,
                ModType = g.Key.ModType,
                Value = g.Sum(m => m.Value) // 동일 그룹의 수치들을 하나로 더함
            });

        // 3. 병합된 결과를 캐시 리스트에 넣습니다.
        _cachedModifiers.AddRange(mergedModifiers);
        _isDirty = false; // 갱신 완료
        
        return _cachedModifiers;
    }
}
