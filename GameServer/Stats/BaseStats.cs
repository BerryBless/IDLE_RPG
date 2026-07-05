namespace GameServer.Stats;

/// <summary>
/// 상한 없이 기하급수적으로 스케일링되는 엔티티의 기본 전투 스탯(체력·공격력·방어력·회복력).
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Memory Allocation:</b> 필드가 모두 <see cref="BigNumber"/>(struct)이므로 인스턴스 자체 외 추가 힙 할당은 없다.</description></item>
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 동시 쓰기가 발생할 수 있는 컨텍스트(전투 갱신 스레드 등)에서는 호출 측이 동기화를 책임진다.</description></item>
/// </list>
/// </remarks>
public sealed class BaseStats
{
    /// <summary>기본 체력.</summary>
    public BigNumber Hp { get; set; }

    /// <summary>기본 공격력.</summary>
    public BigNumber Atk { get; set; }

    /// <summary>기본 방어력.</summary>
    public BigNumber Def { get; set; }

    /// <summary>기본 초당 회복량.</summary>
    public BigNumber Recovery { get; set; }

    /// <summary>기본 최대 마나.</summary>
    public BigNumber Mana { get; set; }

    /// <summary>기본 초당 마나 재생량.</summary>
    public BigNumber ManaRegen { get; set; }

    /// <summary>두 <see cref="BaseStats"/>를 항목별로 합산한 새 인스턴스를 반환한다.</summary>
    /// <param name="a">첫 번째 피연산자</param>
    /// <param name="b">두 번째 피연산자</param>
    /// <returns>항목별 합산 결과</returns>
    public static BaseStats operator +(BaseStats a, BaseStats b) => throw new NotImplementedException();
}
