using GameServer.Items;
using GameServer.Stats;

namespace GameServer.Entities;

/// <summary>사용자 계정이 소유·조작하는 플레이어 캐릭터.</summary>
public sealed class Player : Entity
{
    /// <summary>이 캐릭터를 소유한 계정의 고유 ID.</summary>
    public string AccountId { get; init; } = string.Empty;

    /// <summary>현재 누적 경험치.</summary>
    public BigNumber CurrentExp { get; set; }

    /// <summary>현재 보유 골드.</summary>
    public BigNumber CurrentGold { get; set; }

    /// <summary>착용 장비를 관리하는 인벤토리.</summary>
    public EquipmentInventory Equipment { get; init; } = new();

    /// <summary>착용 장비가 제공하는 스탯 수정치를 반환한다.</summary>
    /// <returns><see cref="Equipment"/>가 제공하는 <see cref="StatModifier"/> 목록</returns>
    protected override List<StatModifier> GetExtraModifiers() => throw new NotImplementedException();

    /// <summary>경험치를 획득하여 <see cref="CurrentExp"/>에 누적한다.</summary>
    /// <param name="amount">획득한 경험치량</param>
    public void AddExp(BigNumber amount) => throw new NotImplementedException();

    /// <summary>골드를 획득하여 <see cref="CurrentGold"/>에 누적한다.</summary>
    /// <param name="amount">획득한 골드량</param>
    public void AddGold(BigNumber amount) => throw new NotImplementedException();
}
