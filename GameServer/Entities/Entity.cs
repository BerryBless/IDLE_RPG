using GameServer.Combat;
using GameServer.Stats;

namespace GameServer.Entities;

/// <summary>
/// 전투에 참여하는 모든 대상(플레이어·몬스터)의 공통 추상 기반 타입.
/// 기본 스탯·특성·버프·최종 스탯 캐시를 소유하고 공통 갱신 파이프라인을 제공한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> <see cref="Update"/>/<see cref="UpdateFinalStats"/>는 전투 갱신 루프(단일 스레드)에서 호출되는 것을 전제로 한다.</description></item>
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 동일 인스턴스에 대한 동시 갱신은 호출 측이 금지해야 한다.</description></item>
/// </list>
/// </remarks>
public abstract class Entity
{
    /// <summary>엔티티 인스턴스를 식별하는 고유 ID.</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>엔티티 레벨.</summary>
    public int Level { get; set; }

    /// <summary>
    /// 장비/버프를 제외한 순수 기본 스탯. 몬스터 템플릿·플레이어 세이브 로드 등 외부에서
    /// 초기값을 주입해야 하므로 public으로 노출한다(스켈레톤 단계에서는 protected였으나,
    /// 외부에서 값을 설정할 경로가 전혀 없어 인스턴스 생성 직후 항상 기본값 0으로 고정되는 문제가 있었다).
    /// </summary>
    public BaseStats BaseStats { get; set; } = new();

    /// <summary>장비/버프를 제외한 순수 기본 특성치. <see cref="BaseStats"/>와 동일한 이유로 public.</summary>
    public Traits BaseTraits { get; set; } = new();

    /// <summary>기본 스탯 + 모든 수정치가 반영된 최종 스탯 캐시.</summary>
    public FinalStats FinalStats { get; init; } = new();

    /// <summary>이 엔티티에 부여된 버프/디버프를 관리하는 매니저.</summary>
    public BuffManager BuffManager { get; init; } = new();

    /// <summary>지정한 양의 피해를 받아 현재 체력을 감소시킨다.</summary>
    /// <param name="amount">받을 피해량</param>
    /// <remarks>
    /// 현재 체력은 0 미만으로 내려가지 않도록 클램프된다. 코드리뷰 F5: 음수 <paramref name="amount"/>가
    /// 회복으로 작동(오버힐 포함)하지 않도록 0으로 클램프한 뒤 차감한다.
    /// </remarks>
    public void TakeDamage(BigNumber amount)
    {
        var safeAmount = Math.Max(0, amount);
        FinalStats.CurrentHp = Math.Max(0, FinalStats.CurrentHp - safeAmount);
    }

    /// <summary>현재 생존 여부. <see cref="Stats.FinalStats.CurrentHp"/>가 0보다 크면 true.</summary>
    public bool IsAlive => FinalStats.CurrentHp > 0;

    /// <summary>
    /// 경과 시간만큼 이 엔티티의 전투 관련 상태(버프 만료·스탯 재계산·자연 회복)를 갱신한다.
    /// </summary>
    /// <param name="deltaTime">이전 갱신 이후 경과한 시간(초)</param>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> 전투 갱신 루프(단일 스레드)에서 호출되는 것을 전제로 한다.</description></item>
    /// </list>
    /// 코드리뷰 F6: 사망(<see cref="IsAlive"/>가 false) 상태에서는 버프 틱·스탯 재계산·회복을 전부
    /// 건너뛴다. 되살리려면 <see cref="RestoreResources"/> 등으로 <c>CurrentHp</c>를 먼저 회복시켜야 한다.
    /// </remarks>
    public void Update(float deltaTime)
    {
        if (!IsAlive)
        {
            return;
        }

        BuffManager.Update(deltaTime);
        UpdateFinalStats();

        FinalStats.CurrentHp = Math.Min(FinalStats.MaxHp, FinalStats.CurrentHp + FinalStats.Recovery * deltaTime);
        FinalStats.CurrentMana = Math.Min(FinalStats.MaxMana, FinalStats.CurrentMana + FinalStats.ManaRegen * deltaTime);
    }

    /// <summary>
    /// 마나가 충분하면 소모하고 성공 여부를 반환한다. 스킬 발동 게이팅에 사용하는 프리미티브.
    /// </summary>
    /// <param name="amount">소모할 마나량</param>
    /// <returns>마나가 충분해 소모에 성공했으면 true, 부족해 실패했으면 false</returns>
    /// <remarks>
    /// 코드리뷰 F5: 음수 <paramref name="amount"/>는 마나를 증가시키는 결과로 이어지므로,
    /// 유효하지 않은 호출로 간주해 항상 실패(false)로 처리한다.
    /// </remarks>
    public bool TryConsumeMana(BigNumber amount)
    {
        if (amount < 0 || FinalStats.CurrentMana < amount)
        {
            return false;
        }
        FinalStats.CurrentMana -= amount;
        return true;
    }

    /// <summary>현재 체력·마나를 각각 최대치로 채운다. 스폰·부활 시 호출한다.</summary>
    public void RestoreResources()
    {
        FinalStats.CurrentHp = FinalStats.MaxHp;
        FinalStats.CurrentMana = FinalStats.MaxMana;
    }

    /// <summary>
    /// <see cref="BaseStats"/>·<see cref="BaseTraits"/>·<see cref="GetExtraModifiers"/>·<see cref="BuffManager"/>의
    /// 수정치를 모두 합산하여 <see cref="FinalStats"/> 캐시를 갱신한다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> 전투 갱신 루프(단일 스레드)에서만 호출되는 것을 전제로 한다. Not Thread-safe.</description></item>
    /// <item><description><b>Memory Allocation:</b> 모디파이어 병합용 임시 리스트를 매 호출마다 할당한다(hot path 최적화는 후속 사이클 검토 대상).</description></item>
    /// </list>
    /// StatType별로 그룹핑한 뒤 <see cref="ModifierType.FlatAdd"/> → <see cref="ModifierType.PercentAdd"/> →
    /// <see cref="ModifierType.PercentMult"/> 순으로 누적 적용한다: <c>final = (base + ΣFlat) × (1 + ΣPercentAdd) × Π(1 + PercentMult_i)</c>.
    /// </remarks>
    public virtual void UpdateFinalStats()
    {
        FinalStats.MaxHp = BaseStats.Hp;
        FinalStats.Atk = BaseStats.Atk;
        FinalStats.Def = BaseStats.Def;
        FinalStats.Recovery = BaseStats.Recovery;
        FinalStats.MaxMana = BaseStats.Mana;
        FinalStats.ManaRegen = BaseStats.ManaRegen;
        // BaseTraits를 참조로 그대로 대입하면 아래 그룹핑 적용이 원본 BaseTraits까지 오염시켜
        // UpdateFinalStats를 반복 호출할 때마다 특성치가 누적된다. 반드시 값 복사본을 사용한다.
        FinalStats.CombatTraits = BaseTraits.Clone();
        // 무기 등에서 오는 공격 배율은 StatModifier 그룹핑(Flat/PercentAdd/PercentMult) 대상이 아니라
        // 하위 타입이 직접 결정하는 값이므로 별도 훅으로 반영한다(코드리뷰 F1: 온라인 CalcFinalDamage와
        // 오프라인 ProcessOfflineTime이 이 값을 동일하게 읽어야 정합성이 유지된다).
        FinalStats.AttackScaling = GetAttackScaling();

        var modifiers = new List<StatModifier>(GetExtraModifiers());
        modifiers.AddRange(BuffManager.GetAllActiveModifiers());

        foreach (var group in modifiers.GroupBy(m => m.StatType))
        {
            double baseValue = GetFinalValue(group.Key);
            double flatSum = 0;
            double percentAddSum = 0;
            double percentMultProduct = 1;

            foreach (var modifier in group)
            {
                switch (modifier.ModType)
                {
                    case ModifierType.FlatAdd:
                        flatSum += modifier.Value;
                        break;
                    case ModifierType.PercentAdd:
                        percentAddSum += modifier.Value;
                        break;
                    case ModifierType.PercentMult:
                        percentMultProduct *= 1 + modifier.Value;
                        break;
                }
            }

            double result = (baseValue + flatSum) * (1 + percentAddSum) * percentMultProduct;
            SetFinalValue(group.Key, result);
        }
    }

    /// <summary><see cref="FinalStats"/>·<see cref="Stats.FinalStats.CombatTraits"/>에서 지정한 스탯의 현재 값을 읽는다.</summary>
    private double GetFinalValue(StatType type) => type switch
    {
        StatType.Hp => FinalStats.MaxHp,
        StatType.Atk => FinalStats.Atk,
        StatType.Def => FinalStats.Def,
        StatType.Recovery => FinalStats.Recovery,
        StatType.Mana => FinalStats.MaxMana,
        StatType.ManaRegen => FinalStats.ManaRegen,
        StatType.AtkSpeed => FinalStats.CombatTraits.AtkSpeed,
        StatType.CritProb => FinalStats.CombatTraits.CritProb,
        StatType.CritDmg => FinalStats.CombatTraits.CritDmg,
        StatType.ArmorPen => FinalStats.CombatTraits.ArmorPen,
        StatType.Lifesteal => FinalStats.CombatTraits.Lifesteal,
        _ => 0
    };

    /// <summary><see cref="FinalStats"/>·<see cref="Stats.FinalStats.CombatTraits"/>에 지정한 스탯의 집계 결과를 반영한다.</summary>
    private void SetFinalValue(StatType type, double value)
    {
        switch (type)
        {
            case StatType.Hp: FinalStats.MaxHp = value; break;
            case StatType.Atk: FinalStats.Atk = value; break;
            case StatType.Def: FinalStats.Def = value; break;
            case StatType.Recovery: FinalStats.Recovery = value; break;
            case StatType.Mana: FinalStats.MaxMana = value; break;
            case StatType.ManaRegen: FinalStats.ManaRegen = value; break;
            case StatType.AtkSpeed: FinalStats.CombatTraits.AtkSpeed = value; break;
            case StatType.CritProb: FinalStats.CombatTraits.CritProb = value; break;
            case StatType.CritDmg: FinalStats.CombatTraits.CritDmg = value; break;
            case StatType.ArmorPen: FinalStats.CombatTraits.ArmorPen = value; break;
            case StatType.Lifesteal: FinalStats.CombatTraits.Lifesteal = value; break;
        }
    }

    /// <summary>
    /// 하위 타입(플레이어의 장비, 몬스터의 어픽스 등) 고유의 추가 수정치 소스를 제공한다.
    /// </summary>
    /// <returns>하위 타입이 기여하는 <see cref="StatModifier"/> 목록</returns>
    protected abstract List<StatModifier> GetExtraModifiers();

    /// <summary>
    /// 이 엔티티의 공격력 배율(주로 장착 무기에서 기인)을 반환한다. 기본 구현은 배율 없음(1.0).
    /// </summary>
    /// <returns><see cref="FinalStats.AttackScaling"/>에 반영될 배율</returns>
    /// <remarks>
    /// 코드리뷰 F1 수정: 이전에는 무기 배율이 <see cref="Systems.BattleManager.CalcFinalDamage"/> 호출부에서
    /// 수동으로 전달되어 <see cref="Systems.OfflineProgressionManager"/>는 이 값을 전혀 알지 못했다.
    /// 이제 <see cref="UpdateFinalStats"/>가 이 훅으로 <see cref="FinalStats.AttackScaling"/>을 채워
    /// 온라인·오프라인 모든 데미지 경로가 동일한 값을 읽는다.
    /// </remarks>
    protected virtual double GetAttackScaling() => 1.0;
}
