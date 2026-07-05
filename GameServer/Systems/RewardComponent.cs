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
    /// <remarks>
    /// 코드리뷰 F11: kill마다 <see cref="LootItem"/>을 개별 생성하지 않고 <c>ItemMetaId</c>별로
    /// 수량을 합산한 뒤 distinct 아이템당 1개씩만 생성한다. 이전 방식은 할당량이 <paramref name="killCount"/>에
    /// 비례해, 오프라인 방치 시간이 길어질수록(코드리뷰 F1로 killCount가 커질 수 있음) 힙 할당이
    /// 무제한으로 커지는 문제가 있었다. 이제 할당량은 <see cref="DropTable"/> 크기에만 비례한다.
    /// </remarks>
    public LootData GenerateLoot(int killCount)
    {
        // 코드리뷰 F3: 음수 killCount(호출측 계산 오류 등으로 유입될 수 있음)가 음수 경험치/골드를
        // 반환하지 않도록 0으로 클램프한다.
        killCount = Math.Max(0, killCount);

        var quantityByItemMetaId = new Dictionary<int, int>();

        for (int kill = 0; kill < killCount; kill++)
        {
            foreach (var pool in DropTable)
            {
                if (_random.NextDouble() >= pool.DropChance)
                {
                    continue;
                }

                // 코드리뷰 F4: MinQty > MaxQty(마스터 데이터 오류)여도 Random.Next가 예외를 던지지
                // 않도록 >=로 완화하고, 이 경우 MinQty를 그대로 사용한다.
                int quantity = pool.MinQty >= pool.MaxQty
                    ? pool.MinQty
                    : _random.Next(pool.MinQty, pool.MaxQty + 1); // Next(min, maxExclusive) → MaxQty 포함하려면 +1

                quantityByItemMetaId[pool.ItemMetaId] = quantityByItemMetaId.GetValueOrDefault(pool.ItemMetaId) + quantity;
            }
        }

        var acquiredItems = quantityByItemMetaId
            .Select(kv => (Items.Item)new LootItem { ItemMetaId = kv.Key, Quantity = kv.Value })
            .ToList();

        return new LootData
        {
            TotalExp = ExpDrop * killCount,
            TotalGold = GoldDrop * killCount,
            AcquiredItems = acquiredItems
        };
    }
}
