global using BigNumber = double; // 임시.

using GameServer.Entities;
using GameServer.Items;
using GameServer.Stats;
using GameServer.Systems;


// 도메인 타입 구성 예시. 2026-07-05 TDD 사이클에서 스탯 집계·전투·보상 로직이 실구현되었고,
// 이어서 BattleLoop이 추가되어 이제 플레이어 vs 몬스터 전투를 실제로 무한히 반복 실행하는
// 예제다(Ctrl+C로 종료). 몬스터는 처치될 때마다 같은 인스턴스가 HP 풀로 재등장하고,
// 플레이어는 사망 시 즉시 무료 부활한다 — 웨이브/스폰/부활 비용은 다음 사이클 대상.

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

// BaseStats.Hp를 부여하지 않으면 MaxHp가 0으로 남아 무한 전투 루프가 즉시 무의미해진다
// (IsAlive가 시작부터 false). 데모용 최소값으로 생존 가능한 상태를 만든다.
player.BaseStats.Hp = 100;
monster.BaseStats.Hp = 30;
monster.BaseStats.Def = 5;

player.UpdateFinalStats();
monster.UpdateFinalStats();
// 무기의 AttackScaling은 UpdateFinalStats() 호출 시 FinalStats.AttackScaling에 자동 반영되므로
// (코드리뷰 F1 수정) 더 이상 호출부에서 수동으로 전달할 필요가 없다.
player.RestoreResources(); // CurrentHp/CurrentMana를 MaxHp/MaxMana로 채워야 루프 시작 시점에 생존 상태가 된다.
monster.RestoreResources();

// BattleLoop.Run은 cancellationToken을 전달하지 않으면 (기본값 CancellationToken.None) 취소될 수
// 없으므로 진짜 무한 루프로 동작한다 — Ctrl+C로만 종료된다.
new BattleLoop().Run(player, monster);