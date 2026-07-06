// BigNumber는 double 별칭이다. 방치형 특성상 수치가 매우 커질 수 있어 전용 struct 도입 여지를
// 남겨둔다 — 실제 도입은 인플레이션이 double 정밀도(약 15~17자리)를 위협하는 시점에 재검토한다.
global using BigNumber = double;

using GameServer.Entities;
using GameServer.Items;
using GameServer.Systems;


// 도메인 타입 구성 예시. 2026-07-05 TDD 사이클에서 스탯 집계·전투·보상 로직이 실구현되었고,
// 이어서 BattleLoop이 추가되어 이제 플레이어 vs 몬스터 전투를 실제로 무한히 반복 실행하는
// 예제다(Ctrl+C로 종료). 몬스터는 처치될 때마다 같은 인스턴스가 HP 풀로 재등장하고,
// 플레이어는 사망 시 즉시 무료 부활한다 — 웨이브/스폰/부활 비용은 다음 사이클 대상.
// 몬스터(MonsterTable, 10종)와 장비(EquipmentTable, 무기·방어구·장신구 각 5종)는 인라인 하드코딩
// 대신 마스터 데이터 테이블 + 팩토리(MonsterFactory/EquipmentFactory)에서 만든다.

// 코드리뷰(2026-07-06) H1 수정: 테이블이 더 이상 static이 아니라 인스턴스라 CreateDefault()로
// 명시적으로 만들어야 한다(정적 생성자가 아니라 일반 메서드라 실패 시 예외가 그대로 전파됨).
var monsterTable = MonsterTable.CreateDefault();
var equipmentTable = EquipmentTable.CreateDefault();
var levelSystem = PlayerLevelSystem.CreateDefault();

// 코드리뷰(2026-07-06) Medium 수정: new Player{} → ApplyLevel → RestoreResources 세 단계를
// 호출부가 직접 순서대로 호출해야 했고, RestoreResources를 빠뜨리면 MaxHp>0인데도 CurrentHp=0으로
// 남아 전투 시작 즉시 사망 상태가 되는 함정이 있었다. PlayerFactory가 이 세 단계를 대신한다
// (MonsterFactory.Create/EquipmentFactory.Create와 대칭).
var player = PlayerFactory.Create(instanceId: "player-0001", accountId: 0, level: 1, levelSystem);

// 몬스터는 더 이상 인라인으로 하드코딩하지 않고 monsterTable(10종 마스터 데이터)에서 골라
// MonsterFactory로 만든다 — 나중에 MonsterTable이 JSON 기반으로 바뀌어도 이 호출부는 그대로다.
var monster = MonsterFactory.Create(monsterTable.GetById(2003)); // 고블린

// 장비도 몬스터와 동일하게 더 이상 인라인 하드코딩하지 않고 equipmentTable(무기·방어구·장신구
// 각 5종, 총 15종 마스터 데이터)에서 골라 EquipmentFactory로 만든다.
player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(4001)), SlotType.Weapon); // 낡은 검
player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(5001)), SlotType.Armor); // 가죽 갑옷
player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(6001)), SlotType.Accessory); // 낡은 반지

// 장비를 장착한 뒤 스탯을 다시 반영해야 하므로 UpdateFinalStats를 재호출한다. PlayerFactory가
// 이미 RestoreResources까지 호출했지만, 장비 장착으로 MaxHp가 늘어난 만큼 CurrentHp도 다시
// 풀피로 채워 전투 시작 시점에 낭비되는 체력이 없게 한다.
player.UpdateFinalStats();
player.RestoreResources();

// 코드리뷰(2026-07-06) H2 수정: BattleLoop.RunAsync는 이제 Thread.Sleep 대신 await Task.Delay로
// 대기해 스레드를 점유하지 않는다. cancellationToken을 전달하지 않으면(기본값 CancellationToken.None)
// 취소될 수 없으므로 진짜 무한 루프로 동작한다 — Ctrl+C로만 종료된다.
await new BattleLoop(levelSystem).RunAsync(player, monster);