using GameServer.Stats;

namespace GameServer.Systems;

/// <summary>
/// <see cref="Entities.Monster"/>가 소유하는 보상 정의. 처치 시 지급할 경험치·골드·아이템 드롭 테이블을 담는다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 내부 <see cref="_random"/>(<see cref="Random"/>)은
/// 스레드 안전하지 않으므로, 오프라인 정산·전투 갱신 등 단일 스레드 컨텍스트에서만 호출하는 것을 전제로 한다.</description></item>
/// <item><description><b>Blocking 여부:</b> 순수 계산만 수행하며 항상 즉시 반환(non-blocking)된다.</description></item>
/// </list>
/// </remarks>
public sealed class RewardComponent
{
    /// <summary>1회 처치당 지급되는 경험치.</summary>
    public BigNumber ExpDrop { get; init; }

    /// <summary>1회 처치당 지급되는 골드.</summary>
    public BigNumber GoldDrop { get; init; }

    /// <summary>아이템 드롭 확률표.</summary>
    public List<DropPool> DropTable { get; init; } = new();

    // System.Random: 내부적으로 상태(seed)를 갖는 의사난수 생성기라 스레드 안전하지 않음.
    // BattleManager와 동일하게 생성자 주입을 허용해 테스트 시 결정적 시드로 드롭 여부를 재현할 수 있게 한다.
    private readonly Random _random;

    public RewardComponent() : this(new Random())
    {
    }

    /// <summary>테스트 등에서 결정적 난수 시퀀스를 주입하기 위한 생성자.</summary>
    /// <param name="random">드롭 확률·수량 판정에 사용할 난수 생성기</param>
    internal RewardComponent(Random random)
    {
        _random = random;
    }

    /// <summary>
    /// 지정한 처치 횟수만큼의 보상(경험치·골드·아이템)을 확률적으로 산출한다.
    /// </summary>
    /// <param name="killCount">처치 횟수 (오프라인 방치 시간 동안의 누적 처치 수 등)</param>
    /// <returns>산출된 보상 데이터</returns>
    public LootData GenerateLoot(int killCount)
    {
        var acquiredItems = new List<Items.Item>();

        for (int kill = 0; kill < killCount; kill++)
        {
            foreach (var pool in DropTable)
            {
                if (_random.NextDouble() >= pool.DropChance)
                {
                    continue;
                }

                int quantity = pool.MinQty == pool.MaxQty
                    ? pool.MinQty
                    : _random.Next(pool.MinQty, pool.MaxQty + 1); // Next(min, maxExclusive) → MaxQty 포함하려면 +1

                acquiredItems.Add(new LootItem { ItemMetaId = pool.ItemMetaId, Quantity = quantity });
            }
        }

        return new LootData
        {
            TotalExp = ExpDrop * killCount,
            TotalGold = GoldDrop * killCount,
            AcquiredItems = acquiredItems
        };
    }
}
