global using BigNumber = double; // 임시.

using GameServer.Entities;
using GameServer.Items;
using GameServer.Stats;
using GameServer.Systems;


// 도메인 타입 구성 예시. 각 클래스는 아직 스켈레톤(메서드 본문 NotImplementedException) 단계이므로
// 여기서는 인스턴스 생성과 구조 연결만 시연하고, 실제 로직 메서드는 호출하지 않는다.

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
var FinalDamage = BattleManager.Instance.CalcFinalDamage(player, monster, player.Equipment.GetWeapon()?.AttackScaling ?? 0);
Console.WriteLine($"total damage = {FinalDamage}");