using GameServer.Entities;
using GameServer.Items;

namespace GameServer.Systems;

/// <summary>
/// 접속 시 임시 플레이어에게 고정 시작 장비(무기 4001/방어구 5001/장신구 6001)를 착용시키는 공용
/// 헬퍼. <see cref="SessionRaidRunner"/>와 <see cref="SessionBattleRunner"/>가 각자 자기 안에
/// 동일한 로직을 verbatim으로 들고 있던 중복(코드리뷰 Low 발견,
/// <c>docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md</c>)을 이 클래스로 흡수한다.
/// 스테이지/스폰너 시스템 도입 전까지의 고정값이라, 그 사이클에서 이 클래스 자체가 대체될 예정이다.
/// </summary>
internal static class StarterGearEquipper
{
    private const int StarterWeaponId = 4001;    // 낡은 검
    private const int StarterArmorId = 5001;     // 가죽 갑옷
    private const int StarterAccessoryId = 6001; // 낡은 반지

    /// <summary>고정 시작 장비 3종을 착용시키고 최종 스탯/자원을 갱신한다.</summary>
    /// <param name="player">장비를 착용할 대상</param>
    /// <param name="equipmentTable">시작 장비 템플릿(4001/5001/6001) 조회용 테이블</param>
    public static void Equip(Player player, EquipmentTable equipmentTable)
    {
        player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(StarterWeaponId)), SlotType.Weapon);
        player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(StarterArmorId)), SlotType.Armor);
        player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(StarterAccessoryId)), SlotType.Accessory);
        player.UpdateFinalStats();
        player.RestoreResources();
    }
}
