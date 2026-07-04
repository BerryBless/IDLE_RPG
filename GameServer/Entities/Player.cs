using GameServer.Items;
using GameServer.Stats;

namespace GameServer.Entities;

/// <summary>사용자 계정이 소유·조작하는 플레이어 캐릭터.</summary>
public sealed class Player : Entity
{
    /// <summary>이 캐릭터를 소유한 계정의 고유 ID.</summary>
    public int AccountId { get; init; }

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

    /// <summary>
    /// <see cref="BaseStats"/>·<see cref="Entity.BaseTraits"/>에 착용 장비의 스탯 수정치를 합산하여
    /// <see cref="Entity.FinalStats"/> 캐시를 갱신한다.
    /// </summary>
    /// <remarks>
    /// <b>[성능 및 동시성 제약 조건]</b>
    /// <list type="bullet">
    /// <item><description><b>Thread Context:</b> 전투 갱신 루프(단일 스레드)에서만 호출되는 것을 전제로 한다. Not Thread-safe.</description></item>
    /// <item><description><b>Memory Allocation:</b> <see cref="Traits.Clone"/> 호출로 매번 새 <see cref="Traits"/> 인스턴스 1개가 할당된다.
    /// <c>BaseTraits</c>를 참조 그대로 재사용하면 아래 제자리 누적으로 원본이 오염되므로 의도된 할당이다.</description></item>
    /// </list>
    /// </remarks>
    public override void UpdateFinalStats()
    {
        // 기본
        FinalStats.MaxHp =  BaseStats.Hp;
        FinalStats.Atk =  BaseStats.Atk;
        FinalStats.Def =  BaseStats.Def;
        FinalStats.Recovery =  BaseStats.Recovery;
        // BaseTraits를 참조로 그대로 대입하면 아래 += 제자리 수정이 원본 BaseTraits까지 오염시켜
        // UpdateFinalStats를 반복 호출할 때마다 특성치가 누적된다. 반드시 값 복사본을 사용한다.
        FinalStats.CombatTraits = BaseTraits.Clone();

        // 장비
        var equipModiList = Equipment.GetAllModifiers();
        foreach (var modi in equipModiList)
        {
            switch (modi.StatType)
            {
                case StatType.Hp:
                    FinalStats.MaxHp += modi.Value;
                    break;
                case StatType.Atk:
                    FinalStats.Atk += modi.Value;
                    break;
                case StatType.Def:
                    FinalStats.Def += modi.Value;
                    break;
                case StatType.Recovery:
                    FinalStats.Recovery += modi.Value;
                    break;
                case StatType.AtkSpeed:
                    FinalStats.CombatTraits.AtkSpeed += modi.Value;
                    break;
                case StatType.CritProb:
                    FinalStats.CombatTraits.CritProb += modi.Value;
                    break;
                case StatType.CritDmg:
                    FinalStats.CombatTraits.CritDmg += modi.Value;
                    break;
                case StatType.ArmorPen:
                    FinalStats.CombatTraits.ArmorPen += modi.Value;
                    break;
                case StatType.Lifesteal:
                    FinalStats.CombatTraits.Lifesteal += modi.Value;
                    break;
                default:
                    //error
                    break;
            }
        }
    }
}
