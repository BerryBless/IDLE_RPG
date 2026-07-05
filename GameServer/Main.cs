global using BigNumber = double; // 임시.

using GameServer.Entities;
using GameServer.Items;
using GameServer.Stats;
using GameServer.Systems;


// 도메인 타입 구성 예시. 2026-07-05 TDD 사이클에서 스탯 집계·전투·보상 로직이 실구현되어,
// 이제 UpdateFinalStats()/CalcFinalDamage() 등 실제 로직 메서드를 호출하는 예제다.

var player = new Player
{
    InstanceId = "player-0001",
    AccountId = 000,
    Level = 1,
        
};

var monster = new Monster
{
    InstanceId = "monster-0001",
    MonsterId = 1001,
    Level = 1,
    Rewards = new RewardComponent
    {
        ExpDrop = 2,
        GoldDrop = 5,
        DropTable =
        [
            new DropPool { ItemMetaId = 2001, DropChance = 0.1f, MinQty = 1, MaxQty = 1 }
        ]
    }
};

var weapon = new Weapon
{
    InstanceId = "item-0001",
    ItemMetaId = 3001,
    Name = "낡은 검",
    AttackScaling = 1.5f,
    BaseModifiers =
    [
        new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value =1 }
    ]
};

var armor = new Armor
{
    InstanceId = "item-0002",
    ItemMetaId = 1001,
    Name = "방어구",
    BaseModifiers =
    [
        new StatModifier { StatType = StatType.Def, ModType = ModifierType.FlatAdd, Value = 5},
        new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 65}
    ]
};
player.Equipment.Equip(weapon, SlotType.Weapon);
player.Equipment.Equip(armor, SlotType.Armor);
player.UpdateFinalStats();
// 무기의 AttackScaling은 UpdateFinalStats() 호출 시 FinalStats.AttackScaling에 자동 반영되므로
// (코드리뷰 F1 수정) 더 이상 호출부에서 수동으로 전달할 필요가 없다.
var FinalDamage = BattleManager.Instance.CalcFinalDamage(player, monster);
Console.WriteLine($"total damage = {FinalDamage}");