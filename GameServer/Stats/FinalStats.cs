namespace GameServer.Stats;

/// <summary>
/// <see cref="BaseStats"/>·<see cref="Traits"/>·장비·버프 수정치를 모두 합산한 최종 캐시 스탯.
/// 매 프레임 재계산하지 않도록 <see cref="Entities.Entity.UpdateFinalStats"/> 호출 시점에만 갱신되는 스냅샷이다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Memory Allocation:</b> 캐시 목적의 클래스. 매 프레임 새로 할당하지 않고 엔티티당 1개 인스턴스를 재사용(in-place 갱신)하는 것을 전제로 한다.</description></item>
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 단일 갱신 스레드에서만 쓰기가 발생하도록 호출 측이 보장해야 한다.</description></item>
/// </list>
/// </remarks>
public sealed class FinalStats
{
    /// <summary>현재 체력.</summary>
    public BigNumber CurrentHp { get; set; }

    /// <summary>최대 체력.</summary>
    public BigNumber MaxHp { get; set; }

    /// <summary>최종 공격력.</summary>
    public BigNumber Atk { get; set; }

    /// <summary>최종 방어력.</summary>
    public BigNumber Def { get; set; }

    /// <summary>최종 초당 회복량.</summary>
    public BigNumber Recovery { get; set; }

    /// <summary>현재 마나.</summary>
    public BigNumber CurrentMana { get; set; }

    /// <summary>최종 최대 마나.</summary>
    public BigNumber MaxMana { get; set; }

    /// <summary>최종 초당 마나 재생량.</summary>
    public BigNumber ManaRegen { get; set; }

    /// <summary>최종 전투 특성치.</summary>
    public Traits CombatTraits { get; set; } = new();
}
