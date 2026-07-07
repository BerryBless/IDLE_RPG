// BigNumber는 double 별칭이다. 방치형 특성상 수치가 매우 커질 수 있어 전용 struct 도입 여지를
// 남겨둔다 — 실제 도입은 인플레이션이 double 정밀도(약 15~17자리)를 위협하는 시점에 재검토한다.
global using BigNumber = double;

using GameServer.Entities;
using GameServer.Items;
using GameServer.Systems;

// 도메인 타입 구성 예시. 다중 플레이어 배틀 스레드 샤딩 사이클(설계: docs/superpowers/specs/
// 2026-07-07-multi-player-battle-sharding-design.md)부터는 "서버에 다수의 플레이어가 동시 접속해
// 각자 독립적으로 전투를 진행"하는 상황을 시뮬레이션한다. 아직 실제 네트워크 세션 계층은 없으므로,
// ThreadCount * PlayersPerThread명의 Player/Monster 쌍을 하드코딩으로 생성해 스레드당
// PlayersPerThread명씩 나눠 맡긴다. 플레이어 간 상호작용(파티/PvP)은 없다 — 완전히 독립된 전투.

const int ThreadCount = 4;        // 조정 가능 — 총 플레이어 수 = ThreadCount * PlayersPerThread
const int PlayersPerThread = 100; // 고정(설계 문서 결정, 스레드당 100명)
var tickInterval = TimeSpan.FromMilliseconds(500);

var monsterTable = MonsterTable.CreateDefault();
var equipmentTable = EquipmentTable.CreateDefault();
var levelSystem = PlayerLevelSystem.CreateDefault();

// BattleLoop: 내부 상태가 PlayerLevelSystem(읽기 전용 마스터 테이블 조회)뿐이라 여러 샤드
// 스레드가 동시에 Tick을 호출해도 안전 — Player/Monster 인스턴스만 샤드마다 독립이면 된다.
var battleLoop = new BattleLoop(levelSystem);

var shards = Enumerable.Range(0, ThreadCount)
    .Select(shardIndex => Enumerable.Range(0, PlayersPerThread)
        .Select(i => CreatePair(shardIndex * PlayersPerThread + i))
        .ToList())
    .ToList();

foreach (var shard in shards)
{
    // Thread: 전용 스레드로 샤드를 격리한다. 샤드 루프는 Thread.Sleep으로 동기 대기하지만,
    // 스레드 풀 작업 항목이 아니라 전용 스레드라 대기 중에도 다른 작업을 막지 않는다.
    // IsBackground=true로 만들어 프로세스 종료(Ctrl+C) 시 매달리지 않게 한다.
    var thread = new Thread(() => RunShard(shard)) { IsBackground = true };
    thread.Start();
}

await Task.Delay(Timeout.Infinite); // Ctrl+C로 종료

(Player Player, Monster Monster) CreatePair(int index)
{
    var player = PlayerFactory.Create(instanceId: $"player-{index:0000}", accountId: index, level: 1, levelSystem);
    player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(4001)), SlotType.Weapon); // 낡은 검
    player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(5001)), SlotType.Armor); // 가죽 갑옷
    player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(6001)), SlotType.Accessory); // 낡은 반지
    player.UpdateFinalStats();
    player.RestoreResources();

    var monster = MonsterFactory.Create(monsterTable.GetById(2003)); // 고블린 — 플레이어마다 독립 인스턴스

    return (player, monster);
}

void RunShard(List<(Player Player, Monster Monster)> shard)
{
    var deltaTime = (float)tickInterval.TotalSeconds;
    while (true)
    {
        foreach (var (player, monster) in shard)
        {
            var result = ShardBattleRunner.TryTick(battleLoop, player, monster, deltaTime, out var exception);
            if (exception != null)
            {
                // 쌍 단위 격리: 이 예외를 여기서 삼키지 않으면 전용 스레드의 미처리 예외가
                // 프로세스 전체를 종료시킨다(백그라운드 스레드 여부 무관).
                Console.WriteLine($"[{player.InstanceId}] Tick 예외: {exception.Message}");
                continue;
            }

            if (result!.Value != BattleTickEvent.None)
            {
                Console.WriteLine(BattleEventLogger.Format(player.InstanceId, result.Value, player));
            }
        }

        Thread.Sleep(tickInterval);
    }
}
