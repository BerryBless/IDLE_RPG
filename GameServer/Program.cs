using GameServer.Entities;
using GameServer.Items;
using GameServer.Stats;
using GameServer.Systems;

// 도메인 타입 구성 예시. 각 클래스는 아직 스켈레톤(메서드 본문 NotImplementedException) 단계이므로
// 여기서는 인스턴스 생성과 구조 연결만 시연하고, 실제 로직 메서드는 호출하지 않는다.

var player = new Player
{
    InstanceId = "player-0001",
    AccountId = "acc-0001",
    Level = 1
};

var monster = new Monster
{
    InstanceId = "monster-0001",
    MonsterId = 1001,
    Level = 1,
    Rewards = new RewardComponent
    {
        ExpDrop = new BigNumber { Coefficient = 1.0, Exponent = 2 },
        GoldDrop = new BigNumber { Coefficient = 5.0, Exponent = 1 },
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
    AttackRange = 1.5f,
    BaseModifiers =
    [
        new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = new BigNumber { Coefficient = 1.0, Exponent = 1 } }
    ]
};

Console.WriteLine($"Player {player.InstanceId} (Lv.{player.Level}) vs Monster {monster.InstanceId} (Lv.{monster.Level})");
Console.WriteLine($"Equipped candidate: {weapon.Name}, range={weapon.AttackRange}");

var offlineProgressionManager = new OfflineProgressionManager();
Console.WriteLine($"Offline system ready: {offlineProgressionManager.GetType().Name}");
