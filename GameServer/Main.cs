global using BigNumber = double; // 임시.

using GameServer.Entities;
using GameServer.Items;
using GameServer.Systems;


// 도메인 타입 구성 예시. 2026-07-05 TDD 사이클에서 스탯 집계·전투·보상 로직이 실구현되었고,
// 이어서 BattleLoop이 추가되어 이제 플레이어 vs 몬스터 전투를 실제로 무한히 반복 실행하는
// 예제다(Ctrl+C로 종료). 몬스터는 처치될 때마다 같은 인스턴스가 HP 풀로 재등장하고,
// 플레이어는 사망 시 즉시 무료 부활한다 — 웨이브/스폰/부활 비용은 다음 사이클 대상.
// 몬스터(MonsterTable, 10종)와 장비(EquipmentTable, 무기·방어구·장신구 각 5종)는 인라인 하드코딩
// 대신 마스터 데이터 테이블 + 팩토리(MonsterFactory/EquipmentFactory)에서 만든다.

var player = new Player
{
    InstanceId = "player-0001",
    AccountId = 000,
};

// 몬스터는 더 이상 인라인으로 하드코딩하지 않고 MonsterTable(10종 마스터 데이터)에서 골라
// MonsterFactory로 만든다 — 나중에 MonsterTable이 JSON 기반으로 바뀌어도 이 호출부는 그대로다.
var monster = MonsterFactory.CreateMonster(MonsterTable.GetById(2003)); // 고블린

// 장비도 몬스터와 동일하게 더 이상 인라인 하드코딩하지 않고 EquipmentTable(무기·방어구·장신구
// 각 5종, 총 15종 마스터 데이터)에서 골라 EquipmentFactory로 만든다.
player.Equipment.Equip(EquipmentFactory.Create(EquipmentTable.GetById(4001)), SlotType.Weapon); // 낡은 검
player.Equipment.Equip(EquipmentFactory.Create(EquipmentTable.GetById(5001)), SlotType.Armor); // 가죽 갑옷
player.Equipment.Equip(EquipmentFactory.Create(EquipmentTable.GetById(6001)), SlotType.Accessory); // 낡은 반지

// 플레이어 레벨(1~10)도 LevelTable 마스터 데이터에서 가져온다. ApplyLevel이 BaseStats.Hp/Atk/Def
// 세팅과 UpdateFinalStats() 호출까지 대신해준다 — 그래야 MaxHp가 0으로 남아 무한 전투 루프가
// 즉시 무의미해지는 것(IsAlive가 시작부터 false)을 막을 수 있다.
// monster는 MonsterFactory.CreateMonster가 이미 UpdateFinalStats/RestoreResources까지 호출해
// 즉시 전투 투입 가능한 상태로 반환하므로, player만 마저 세팅한다.
PlayerLevelSystem.ApplyLevel(player, 1);
// 무기의 AttackScaling은 UpdateFinalStats() 호출 시 FinalStats.AttackScaling에 자동 반영되므로
// (코드리뷰 F1 수정) 더 이상 호출부에서 수동으로 전달할 필요가 없다.
player.RestoreResources(); // CurrentHp/CurrentMana를 MaxHp/MaxMana로 채워야 루프 시작 시점에 생존 상태가 된다.

// BattleLoop.Run은 cancellationToken을 전달하지 않으면 (기본값 CancellationToken.None) 취소될 수
// 없으므로 진짜 무한 루프로 동작한다 — Ctrl+C로만 종료된다.
new BattleLoop().Run(player, monster);