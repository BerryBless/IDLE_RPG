// BigNumber는 double 별칭이다. 방치형 특성상 수치가 매우 커질 수 있어 전용 struct 도입 여지를
// 남겨둔다 — 실제 도입은 인플레이션이 double 정밀도(약 15~17자리)를 위협하는 시점에 재검토한다.
global using BigNumber = double;

using GameServer.Entities;
using GameServer.Items;
using GameServer.Systems;
using System.Threading.Channels;

// 도메인 타입 구성 예시. 다중 플레이어 배틀 스레드 샤딩 사이클(설계: docs/superpowers/specs/
// 2026-07-07-multi-player-battle-sharding-design.md)부터는 "서버에 다수의 플레이어가 동시 접속해
// 각자 독립적으로 전투를 진행"하는 상황을 시뮬레이션한다. 아직 실제 네트워크 세션 계층은 없으므로,
// ThreadCount * PlayersPerThread명의 Player/Monster 쌍을 하드코딩으로 생성해 스레드당
// PlayersPerThread명씩 나눠 맡긴다. 플레이어 간 상호작용(파티/PvP)은 없다 — 완전히 독립된 전투.
//
// 2026-07-07 종합 코드 리뷰(docs/code-reviews/2026-07-07-multi-player-battle-sharding-review.md)
// High 2건 수정: (1) CancellationToken을 되살려 Ctrl+C 시 각 샤드가 스스로 종료하도록 복원,
// (2) 샤드 스레드들이 Console.WriteLine을 직접 호출해 전역 락에서 직렬화되던 문제를 Channel<T>
// 기반 단일 로그 소비 스레드로 우회.

const int ThreadCount = 4;        // 조정 가능 — 총 플레이어 수 = ThreadCount * PlayersPerThread
const int PlayersPerThread = 100; // 고정(설계 문서 결정, 스레드당 100명)
var tickInterval = TimeSpan.FromMilliseconds(500);

var monsterTable = MonsterTable.CreateDefault();
var equipmentTable = EquipmentTable.CreateDefault();
var levelSystem = PlayerLevelSystem.CreateDefault();

// BattleLoop: 내부 상태가 PlayerLevelSystem(읽기 전용 마스터 테이블 조회)뿐이라 여러 샤드
// 스레드가 동시에 Tick을 호출해도 안전 — Player/Monster 인스턴스만 샤드마다 독립이면 된다.
var battleLoop = new BattleLoop(levelSystem);

// Channel<string>: lock-free MPSC 큐로 구현되어 있어 다수 샤드 스레드(생산자) → 단일 로그 소비
// 스레드(소비자) 경로에서 Console.Out의 전역 락 경합 없이 로그 문자열을 전달한다(코드리뷰
// 2026-07-07 성능 High 수정 — 이전에는 모든 샤드 스레드가 직접 Console.WriteLine을 호출해
// 프로세스 전역 락에서 직렬화되어 샤딩의 병렬성 이점이 상쇄됐다).
var logChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
{
    SingleReader = true,
    SingleWriter = false
});

var logConsumerTask = Task.Run(async () =>
{
    await foreach (var line in logChannel.Reader.ReadAllAsync())
    {
        Console.WriteLine(line);
    }
});

// CancellationTokenSource: Ctrl+C(SIGINT) 기본 동작인 즉시 프로세스 종료 대신, 각 샤드 전용
// 스레드가 다음 대기 지점(WaitHandle.WaitOne)에서 스스로 루프를 빠져나가는 협조적 취소 신호로
// 바꾼다(코드리뷰 2026-07-07 아키텍처 High 수정 — 이전에는 while(true)뿐이라 정상 종료 수단이
// 프로세스 강제 종료밖에 없었다).
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // 기본 강제 종료를 막고, 대신 취소 토큰으로 각 샤드가 정리할 시간을 준다
    cts.Cancel();
};

var shards = Enumerable.Range(0, ThreadCount)
    .Select(shardIndex => Enumerable.Range(0, PlayersPerThread)
        .Select(i => CreatePair(shardIndex * PlayersPerThread + i))
        .ToList())
    .ToList();

foreach (var shard in shards)
{
    // Thread: 전용 스레드로 샤드를 격리한다. 샤드 루프는 WaitHandle.WaitOne으로 동기 대기하지만,
    // 스레드 풀 작업 항목이 아니라 전용 스레드라 대기 중에도 다른 작업을 막지 않는다.
    // IsBackground=true는 정상 취소 경로를 놓치는 비정상 상황에서도 프로세스가 매달리지 않게
    // 하는 안전망이다(정상 경로는 CancellationToken으로 각 샤드가 스스로 종료한다).
    var thread = new Thread(() => RunShard(shard, cts.Token)) { IsBackground = true };
    thread.Start();
}

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C로 정상 종료 진입 — 아래에서 로그 채널을 닫고 남은 로그를 flush한다.
}

logChannel.Writer.TryComplete();
await logConsumerTask;

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
                logChannel.Writer.TryWrite($"[{player.InstanceId}] Tick 예외: {exception.Message}");
                continue;
            }

            if (result!.Value != BattleTickEvent.None)
            {
                logChannel.Writer.TryWrite(BattleEventLogger.Format(player.InstanceId, result.Value, player));
            }
        }

        // 취소 토큰을 감시하며 대기 — Thread.Sleep과 달리 취소 시 즉시 깨어나 루프를 빠져나간다.
        cancellationToken.WaitHandle.WaitOne(tickInterval);
    }
}
