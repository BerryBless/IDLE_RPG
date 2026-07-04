namespace GameServer.Items;

/// <summary>
/// 게임 내 모든 아이템의 최상위 추상 타입. 장비뿐 아니라 소모품 등 향후 확장될
/// 비장비 아이템도 이 타입을 상속하는 것을 전제로 한다.
/// </summary>
public abstract class Item
{
    /// <summary>이 아이템 인스턴스를 식별하는 고유 ID (인벤토리 슬롯 단위 구분용).</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>아이템 정의 테이블(마스터 데이터)을 가리키는 메타 ID.</summary>
    public int ItemMetaId { get; init; }

    /// <summary>아이템 표시 이름.</summary>
    public string Name { get; init; } = string.Empty;
}
