namespace GameServer.Stats;

/// <summary>
/// 가수(coefficient)·지수(exponent) 쌍으로 표현되는 무한 스케일링 수치 값 객체.
/// 방치형 게임에서 흔한 10^300 이상의 기하급수적 스탯 성장을 <see cref="double"/> 오버플로 없이 표현한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Memory Allocation:</b> <c>readonly struct</c>. 힙 할당 없이 스택/인라인에 저장되며, 값 복사 시 GC 압력이 발생하지 않는다.</description></item>
/// <item><description><b>Thread Safety:</b> 불변(immutable) 값 객체. 모든 연산은 새 인스턴스를 반환하므로 스레드 간 공유해도 안전(Thread-safe)하다.</description></item>
/// <item><description><b>Blocking 여부:</b> 순수 계산만 수행하며 항상 즉시 반환(non-blocking)된다.</description></item>
/// </list>
/// </remarks>
public readonly struct BigNumber
{
    /// <summary>정규화된 가수 부분. 일반적으로 [1.0, 10.0) 범위를 유지한다.</summary>
    public double Coefficient { get; init; }

    /// <summary>10의 거듭제곱 지수 부분.</summary>
    public int Exponent { get; init; }

    /// <summary>이 값과 <paramref name="other"/>를 더한 새 <see cref="BigNumber"/>를 반환한다.</summary>
    /// <param name="other">더할 대상 값</param>
    /// <returns>가수 정규화가 적용된 합산 결과</returns>
    public BigNumber Add(BigNumber other) => throw new NotImplementedException();

    /// <summary>이 값과 <paramref name="other"/>를 곱한 새 <see cref="BigNumber"/>를 반환한다.</summary>
    /// <param name="other">곱할 대상 값</param>
    /// <returns>가수 정규화가 적용된 곱셈 결과</returns>
    public BigNumber Multiply(BigNumber other) => throw new NotImplementedException();
}
