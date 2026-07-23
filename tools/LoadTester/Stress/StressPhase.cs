namespace LoadTester.Stress;

/// <summary>스트레스 실행 페이즈.</summary>
public enum StressPhase
{
    /// <summary>기준선: 프로브만 구동해 정상 상태를 측정.</summary>
    Baseline,

    /// <summary>스트레스 구동 중(프로브 병행).</summary>
    During,

    /// <summary>스트레스 해제·정리.</summary>
    Release,

    /// <summary>회복 관측: 프로브만 두고 기준선 복귀를 측정.</summary>
    Recovery,
}
