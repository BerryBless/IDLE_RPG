using GameServer.Items;

namespace GameServer.Systems;

/// <summary>
/// 드롭 테이블 롤 결과로 생성된 범용 루팅 아이템. 정확한 하위 타입(무기/방어구 등)으로의
/// 구체화는 <see cref="DropPool.ItemMetaId"/> 기준 마스터 데이터 조회 시스템(후속 사이클)이 담당하며,
/// 그 전까지 <see cref="RewardComponent.GenerateLoot"/>가 반환할 최소한의 아이템 표현이 필요해 신규로 정의했다.
/// <see cref="DropPool"/>과 동일하게, 다이어그램/스캐폴딩에는 참조만 되고 정의되지 않았던 타입을 보완한다.
/// </summary>
public sealed class LootItem : Item
{
    /// <summary>드롭된 수량.</summary>
    public int Quantity { get; init; }
}
