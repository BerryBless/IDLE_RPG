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
//
// 2026-07-07 공유 레이드 보스 사이클(docs/superpowers/plans/2026-07-07-raid-boss.md): RaidShardIndex
// 샤드의 플레이어들은 개인 몬스터 대신 하나의 공유 보스를 함께 공격한다.
//
// 2026-07-07 관측성 전환(docs/superpowers/plans/2026-07-07-observability.md): 콘솔 출력을 완전히
// 제거하고 GameEventSink(System.Diagnostics.Metrics 카운터/게이지 + NDJSON 파일 로그)로 대체했다.
// dotnet-counters monitor -p <pid> --counters IdleRpg.GameServer 로 실시간 관측 가능.

const int ThreadCount = 4;        // 조정 가능 — 총 플레이어 수 = ThreadCount * PlayersPerThread
const int PlayersPerThread = 100; // 고정(설계 문서 결정, 스레드당 100명)
const int RaidShardIndex = 0;     // 샤드 0만 공유 레이드 보스 참여, 나머지는 기존 개인 전투 유지
var tickInterval = TimeSpan.FromMilliseconds(500);
var raidTimeLimit = TimeSpan.FromSeconds(30); // 이 시간 내에 못 잡으면 레이드 실패(데모용 상수)

var monsterTable = MonsterTable.CreateDefault();
var equipmentTable = EquipmentTable.CreateDefault();
var levelSystem = PlayerLevelSystem.CreateDefault();

// BattleLoop: 내부 상태가 PlayerLevelSystem(읽기 전용 마스터 테이블 조회)뿐이라 여러 샤드
// 스레드가 동시에 Tick을 호출해도 안전 — Player/Monster 인스턴스만 샤드마다 독립이면 된다.
var battleLoop = new BattleLoop(levelSystem);

// GameEventSink: 다수 샤드/레이드 액터(생산자)가 Record*로 메트릭+NDJSON 라인을 밀어넣고, 내부
// 단일 소비자 태스크가 파일에 flush한다(기존 logChannel+logConsumerTask를 재사용 클래스로 승격).
await using var sink = GameEventSink.CreateFile(Path.Combine("logs", "game-events.ndjson"));

// CancellationTokenSource: Ctrl+C(SIGINT) 기본 동작인 즉시 프로세스 종료 대신, 각 샤드 전용
// 스레드가 다음 대기 지점(WaitHandle.WaitOne)에서 스스로 루프를 빠져나가는 협조적 취소 신호로
// 바꾼다(코드리뷰 2026-07-07 아키텍처 High 수정 — 이전에는 while(true)뿐이라 정상 종료 수단이
// 프로세스 강제 종료밖에 없었다). 레이드 액터도 동일 토큰으로 종료된다.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // 기본 강제 종료를 막고, 대신 취소 토큰으로 각 샤드가 정리할 시간을 준다
    cts.Cancel();
};

// 레이드 보스: MonsterFactory.Create가 스폰 시 1회 UpdateFinalStats+RestoreResources를 마친
// 상태로 반환한다. 이후에는 RaidEncounter의 절대 규칙에 따라 이 인스턴스에 Update/UpdateFinalStats를
// 다시는 호출하지 않는다 — 샤드 스레드가 동시에 읽는 Def/CombatTraits를 재기록하면 값이 같아도
// 데이터 레이스가 되기 때문이다.
var raidBoss = MonsterFactory.Create(monsterTable.GetById(7001));
var raid = new RaidEncounter(raidBoss, raidTimeLimit);

// Task.Run: 레이드 액터를 전용 백그라운드 태스크로 띄운다. RunAsync는 await 지점에서만
// 대기하므로 스레드 풀 스레드를 점유하지 않는다.
var raidActorTask = Task.Run(() => raid.RunAsync(sink, cts.Token));

for (int shardIndex = 0; shardIndex < ThreadCount; shardIndex++)
{
    if (shardIndex == RaidShardIndex)
    {
        var raidPlayers = Enumerable.Range(0, PlayersPerThread)
            .Select(i => CreateRaidPlayer(shardIndex * PlayersPerThread + i))
            .ToList();
        // Thread: 레이드 샤드도 개인 전투 샤드와 동일한 전용 스레드 격리 근거(WaitHandle.WaitOne 동기
        // 대기, IsBackground 안전망)를 그대로 따른다.
        var raidThread = new Thread(() => RunRaidShard(raidPlayers, raidBoss, raid, cts.Token)) { IsBackground = true };
        raidThread.Start();
    }
    else
    {
        // shard: for 루프 변수 shardIndex를 그대로 클로저에서 캡처하면 안 된다 — foreach와 달리
        // for 루프 변수는 반복마다 새로 스코프되지 않고 전체 루프에서 단일 변수를 공유하므로,
        // 스레드가 실제로 시작되는 시점(비동기)에는 루프가 이미 끝나버린 값을 참조하게 된다(실측:
        // ArgumentOutOfRangeException). 이 로컬 변수는 반복마다 새로 계산·선언되므로 클로저가
        // 이번 반복의 값을 안전하게 캡처한다. 레이드 샤드(RaidShardIndex)는 개인 몬스터가 필요
        // 없으므로 이 목록에서 처음부터 제외한다(만들어놓고 버리는 낭비 방지).
        var shard = Enumerable.Range(0, PlayersPerThread)
            .Select(i => CreatePair(shardIndex * PlayersPerThread + i))
            .ToList();
        // Thread: 전용 스레드로 샤드를 격리한다. 샤드 루프는 WaitHandle.WaitOne으로 동기 대기하지만,
        // 스레드 풀 작업 항목이 아니라 전용 스레드라 대기 중에도 다른 작업을 막지 않는다.
        // IsBackground=true는 정상 취소 경로를 놓치는 비정상 상황에서도 프로세스가 매달리지 않게
        // 하는 안전망이다(정상 경로는 CancellationToken으로 각 샤드가 스스로 종료한다).
        var thread = new Thread(() => RunShard(shard, cts.Token)) { IsBackground = true };
        thread.Start();
    }
}

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C로 정상 종료 진입 — 아래에서 싱크를 닫고 남은 메트릭/로그를 flush한다.
}

// 레이드 액터를 먼저 기다린다: 액터가 sink에 마지막 처치/실패 라인을 쓸 수 있으므로,
// sink를 닫기 전에 액터가 끝나야 그 로그가 유실되지 않는다.
await raidActorTask;
// await using으로 선언했으므로 sink.DisposeAsync()는 스코프 종료 시 자동 호출된다.

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

Player CreateRaidPlayer(int index)
{
    // CreatePair와 동일한 장비 세팅이나, 개인 몬스터를 만들지 않는다(레이드 샤드는 공유 보스만 공격).
    var player = PlayerFactory.Create(instanceId: $"player-{index:0000}", accountId: index, level: 1, levelSystem);
    player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(4001)), SlotType.Weapon);
    player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(5001)), SlotType.Armor);
    player.Equipment.Equip(EquipmentFactory.Create(equipmentTable.GetById(6001)), SlotType.Accessory);
    player.UpdateFinalStats();
    player.RestoreResources();
    return player;
}

void RunShard(List<(Player Player, Monster Monster)> shard, CancellationToken cancellationToken)
{
    var deltaTime = (float)tickInterval.TotalSeconds;
    while (!cancellationToken.IsCancellationRequested)
    {
        foreach (var (player, monster) in shard)
        {
            var result = ShardBattleRunner.TryTick(battleLoop, player, monster, deltaTime, out var exception);
            if (exception != null)
            {
                // 쌍 단위 격리: 이 예외를 여기서 삼키지 않으면 전용 스레드의 미처리 예외가
                // 프로세스 전체를 종료시킨다(백그라운드 스레드 여부 무관).
                sink.RecordTickException(player.InstanceId, exception);
                continue;
            }

            switch (result!.Value)
            {
                case BattleTickEvent.MonsterDefeated:
                    sink.RecordMonsterDefeated(player.InstanceId, player.Level, player.CurrentExp, player.CurrentGold);
                    break;
                case BattleTickEvent.PlayerDefeated:
                    sink.RecordPlayerDefeated(player.InstanceId);
                    break;
            }
        }

        // 취소 토큰을 감시하며 대기 — Thread.Sleep과 달리 취소 시 즉시 깨어나 루프를 빠져나간다.
        cancellationToken.WaitHandle.WaitOne(tickInterval);
    }
}

void RunRaidShard(List<Player> players, Monster sharedBoss, RaidEncounter encounter, CancellationToken cancellationToken)
{
    var deltaTime = (float)tickInterval.TotalSeconds;
    var playersById = players.ToDictionary(p => p.InstanceId);
    var rewardReader = encounter.RewardReader;

    while (!cancellationToken.IsCancellationRequested)
    {
        // 1) 액터가 되돌려준 보상 grant를 이 스레드(소유 스레드)에서 적용한다 — Player.AddExp/AddGold
        //    변경은 그 Player를 소유한 이 스레드에서만 수행(단일 소유 원칙).
        while (rewardReader.TryRead(out var grant))
        {
            if (playersById.TryGetValue(grant.PlayerInstanceId, out var owner))
            {
                owner.AddExp(grant.Exp);
                owner.AddGold(grant.Gold);
            }
        }

        // 2) 각 플레이어의 보스 피해를 계산해 fire-and-forget으로 전송한다. 보스의 CurrentHp/IsAlive는
        //    절대 읽지 않는다 — 액터 스레드가 쓰는 값과의 레이스를 피하기 위해서다.
        foreach (var player in players)
        {
            try
            {
                player.Update(deltaTime); // 자기 Player만 갱신(보스는 Update하지 않음 — 위 절대 규칙 참고)
                var damage = BattleManager.Instance.CalcFinalDamage(player, sharedBoss); // 보스의 불변 Def/ArmorPen만 읽음
                encounter.SubmitDamage(player.InstanceId, damage);
            }
            catch (Exception ex)
            {
                sink.RecordTickException(player.InstanceId, ex);
            }
        }

        cancellationToken.WaitHandle.WaitOne(tickInterval);
    }
}
