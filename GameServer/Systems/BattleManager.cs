using GameServer.Entities;
using GameServer.Stats;

namespace GameServer.Systems;

/// <summary>
/// 두 엔티티 간의 전투 데미지 계산을 담당하는 전역 시스템.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 내부 <see cref="_random"/>(<see cref="Random"/>)은
/// 스레드 안전하지 않으므로, 전투 갱신 루프(단일 스레드)에서만 호출하는 것을 전제로 한다.
/// 여러 스레드에서 동시 호출해야 한다면 스레드별 인스턴스 또는 <c>Random.Shared</c>(.NET 6+, 스레드 안전) 도입이 필요하다.</description></item>
/// <item><description><b>Blocking 여부:</b> 순수 계산만 수행하며 항상 즉시 반환(non-blocking)된다.</description></item>
/// </list>
/// <b>[BigNumber 임시 결합 주의]</b>
/// 현재 <c>BigNumber</c>는 <c>Main.cs</c>의 <c>global using BigNumber = double;</c> 임시 별칭이다.
/// 이 메서드의 <c>Math.Max</c>/사칙연산은 모두 <see cref="double"/> 연산자에 의존하므로,
/// <c>Stats/BigNumber.cs</c>의 실제 struct(가수·지수 표현)가 활성화되면 컴파일이 깨진다.
/// 그 시점에는 <see cref="BigNumber.Add"/>/<see cref="BigNumber.Multiply"/> API로 재작성해야 한다.
/// </remarks>
public sealed class BattleManager
{
    /// <summary>방어력 감쇠 공식 <c>100 / (Def + 100)</c>에 쓰이는 기준 상수.
    /// 방어력이 이 값과 같을 때 최종 데미지가 절반이 되도록 하는 밸런싱 계수.</summary>
    private const double DefenseConstant = 100;

    // 정적 필드 초기화는 CLR의 타입 초기화 락으로 보호되어 최초 1회만 생성됨이 보장된다(Thread-safe).
    // 이전의 `_instance ??= new BattleManager()` (지연 초기화) 패턴은 널 검사와 대입이 원자적이지 않아
    // 동시 접근 시 인스턴스가 중복 생성될 수 있었다.
    private static readonly BattleManager _instance = new();

    /// <summary>전역 유일 인스턴스.</summary>
    public static BattleManager Instance => _instance;

    // System.Random: 내부적으로 상태(seed)를 갖는 의사난수 생성기라 스레드 안전하지 않음.
    // 생성자 주입을 허용해 테스트 시 결정적 시드로 크리티컬 판정을 재현할 수 있게 한다.
    private readonly Random _random;

    private BattleManager() : this(new Random())
    {
    }

    /// <summary>테스트 등에서 결정적 난수 시퀀스를 주입하기 위한 생성자.</summary>
    /// <param name="random">크리티컬 판정에 사용할 난수 생성기</param>
    internal BattleManager(Random random)
    {
        _random = random;
    }

    /// <summary>
    /// 공격자가 방어자에게 입히는 최종 데미지를 계산한다.
    /// </summary>
    /// <param name="attacker">공격을 가하는 엔티티</param>
    /// <param name="target">공격을 받는 엔티티</param>
    /// <param name="attackScaling">
    /// 이번 공격에 한해 무기 배율 위에 추가로 곱해지는 배율(예: 스킬 계수). 기본 1.0(추가 배율 없음).
    /// 무기 등 장비에서 오는 공격력 배율은 더 이상 이 파라미터로 전달하지 않는다 — 코드리뷰 F1 수정으로
    /// <see cref="Entities.Entity.UpdateFinalStats"/>가 <see cref="FinalStats.AttackScaling"/>에 자동 반영하므로,
    /// 온라인(이 메서드)·오프라인(<see cref="OfflineProgressionManager"/>) 양쪽이 항상 동일한 값을 읽는다.
    /// </param>
    /// <returns>방어력 감쇠와 치명타가 반영된 최종 데미지 (0 이상)</returns>
    /// <exception cref="ArgumentNullException"><paramref name="attacker"/> 또는 <paramref name="target"/>이 null인 경우</exception>
    /// <remarks>
    /// <paramref name="attacker"/>·<paramref name="target"/>의 <see cref="Entity.FinalStats"/>가
    /// 호출 시점 기준 최신 상태라고 가정한다. 호출 전에 <see cref="Entity.UpdateFinalStats"/>를
    /// 먼저 호출해 갱신하는 책임은 호출 측에 있다.
    /// </remarks>
    public BigNumber CalcFinalDamage(Entity attacker, Entity target, float attackScaling = 1f)
    {
        ArgumentNullException.ThrowIfNull(attacker);
        ArgumentNullException.ThrowIfNull(target);

        FinalStats statsAttacker = attacker.FinalStats;
        FinalStats statsTarget = target.FinalStats;

        // 공격 배율: 무기 등에서 오는 배율(FinalStats.AttackScaling, 자동 반영)에 이번 공격 고유의
        // 추가 배율(attackScaling 파라미터, 예: 스킬 계수)을 곱한다.
        BigNumber finalDamage = Math.Max(statsAttacker.Atk, 0) * statsAttacker.AttackScaling * attackScaling;

        // 크리: CritDmg는 "추가로 곱해지는 배율"로 취급한다 (예: CritDmg=0.5 → 크리 시 1.5배).
        if (IsCritical(statsAttacker.CombatTraits.CritProb))
        {
            finalDamage += finalDamage * statsAttacker.CombatTraits.CritDmg;
        }

        // 뎀감: 방어력에서 방어 관통을 뺀 유효 방어력 기준으로 감쇠 배율을 산출.
        // 주의: DefenseConstant/(x+DefenseConstant) 형태이므로 Def·ArmorPen이 정수 타입으로 바뀌면
        // 정수 나눗셈으로 인해 결과가 0이 될 수 있다 (현재는 BigNumber=double 별칭이라 안전).
        var defMult = DefenseConstant / (Math.Max(0, statsTarget.Def - statsAttacker.CombatTraits.ArmorPen) + DefenseConstant);
        finalDamage *= defMult;

        return finalDamage;
    }

    /// <summary>주어진 치명타 확률에 따라 이번 공격이 치명타로 판정되는지 여부를 굴린다.</summary>
    /// <param name="critProb">치명타 확률 (0.0 ~ 1.0)</param>
    /// <returns>치명타 여부</returns>
    private bool IsCritical(double critProb) => _random.NextDouble() < critProb;
}
