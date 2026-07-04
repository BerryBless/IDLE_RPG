namespace GameServer.Stats;

/// <summary>
/// 상한 또는 비율 제약이 존재하는 엔티티의 전투 특성치(공격 속도·치명타·방어 관통·흡혈).
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Memory Allocation:</b> 필드가 모두 <see cref="double"/>이므로 인스턴스 자체 외 추가 힙 할당은 없다.</description></item>
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 호출 측이 동기화를 책임진다.</description></item>
/// </list>
/// </remarks>
public sealed class Traits
{
    /// <summary>공격 속도.</summary>
    public double AtkSpeed { get; set; }

    /// <summary>치명타 확률 </summary>
    public double CritProb { get; set; }

    /// <summary>치명타 피해량 배율.</summary>
    public double CritDmg { get; set; }

    /// <summary>방어 관통.</summary>
    public double ArmorPen { get; set; }

    /// <summary>흡혈률</summary>
    public double Lifesteal { get; set; }

    /// <summary>두 <see cref="Traits"/>를 항목별로 합산한 새 인스턴스를 반환한다.</summary>
    /// <param name="a">첫 번째 피연산자</param>
    /// <param name="b">두 번째 피연산자</param>
    /// <returns>항목별 합산 결과 (상한 클램프 적용 전)</returns>
    public static Traits operator +(Traits a, Traits b) => throw new NotImplementedException();
}
