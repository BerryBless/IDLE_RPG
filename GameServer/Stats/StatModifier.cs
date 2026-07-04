namespace GameServer.Stats;

/// <summary>
/// 장비·버프 등에서 발생하여 엔티티의 특정 스탯 한 항목에 적용되는 단일 수정치.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Memory Allocation:</b> 불변 데이터를 담는 경량 클래스. 인스턴스는 장비/버프 생성 시 1회만 할당되고 이후 재사용된다.</description></item>
/// <item><description><b>Thread Safety:</b> 생성 후 값이 변경되지 않는 불변 객체로 사용하는 것을 전제로 한다 (Thread-safe).</description></item>
/// </list>
/// </remarks>
public sealed class StatModifier
{
    /// <summary>수정치가 적용될 대상 스탯.</summary>
    public StatType StatType { get; init; }

    /// <summary>수정치의 연산 방식.</summary>
    public ModifierType ModType { get; init; }

    /// <summary>적용될 수치 값.</summary>
    public BigNumber Value { get; init; }
}
