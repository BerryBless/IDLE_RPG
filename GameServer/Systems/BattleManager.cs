using GameServer.Entities;
using GameServer.Stats;

namespace GameServer.Systems;

public class BattleManager
{
    private static BattleManager  _instance;
    public static BattleManager Instance => _instance ??= new BattleManager();


    public BigNumber CalcFinalDamage(Entity attacker, Entity target, float attackScaling = 1f)
    {
        FinalStats statsAttacker = attacker.FinalStats;
        FinalStats statsTarget = target.FinalStats;

        // 공격 배율
        BigNumber finalDamage = Math.Max(statsAttacker.Atk ,0) * attackScaling;
        
        // 크리
        if (IsCritical(statsAttacker.CombatTraits.CritProb))
        {
            finalDamage += finalDamage * statsAttacker.CombatTraits.CritDmg ;
        }
        
        // 뎀감
        var defMult = 100 / (Math.Max(0, statsTarget.Def - statsAttacker.CombatTraits.ArmorPen) + 100);
        finalDamage *= defMult;
        
        return finalDamage;
    }
    
    private bool IsCritical(double critProb)
    {
        return true;
    }


}