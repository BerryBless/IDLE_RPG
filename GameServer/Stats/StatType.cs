namespace GameServer.Stats;

/// <summary>엔티티가 보유할 수 있는 전투 스탯의 종류.</summary>
public enum StatType
{
    /// <summary>체력.</summary>
    Hp,

    /// <summary>공격력.</summary>
    Atk,

    /// <summary>방어력.</summary>
    Def,

    /// <summary>초당 자연 회복량.</summary>
    Recovery,

    /// <summary>공격 속도.</summary>
    AtkSpeed,

    /// <summary>치명타 확률.</summary>
    CritProb,

    /// <summary>치명타 피해량 배율.</summary>
    CritDmg,

    /// <summary>방어 관통.</summary>
    ArmorPen,

    /// <summary>흡혈률.</summary>
    Lifesteal
}
