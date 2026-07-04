namespace GameServer.Stats;

/// <summary>
/// <see cref="StatModifier"/>가 대상 스탯에 적용되는 연산 방식.
/// 최종 집계 시 <see cref="FlatAdd"/> → <see cref="PercentAdd"/> → <see cref="PercentMult"/> 순으로 누적 적용하는 것을 전제로 한다.
/// </summary>
public enum ModifierType
{
    /// <summary>고정 수치 가산.</summary>
    FlatAdd,

    /// <summary>가산형 퍼센트 보너스 (동일 그룹끼리 합산 후 적용).</summary>
    PercentAdd,

    /// <summary>곱연산형 퍼센트 보너스 (다른 보너스 적용 후 최종 배율로 곱연산).</summary>
    PercentMult
}
