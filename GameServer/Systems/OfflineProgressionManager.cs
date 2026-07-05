using GameServer.Entities;

namespace GameServer.Systems;

/// <summary>
/// 플레이어가 접속하지 않은 동안의 방치 진행(오프라인 사냥)을 계산하는 전역 시스템.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 싱글턴으로 사용할 경우 호출 측에서 동시 호출을 직렬화해야 한다.</description></item>
/// <item><description><b>Blocking 여부:</b> 순수 계산 로직으로 설계되어야 하며, 내부에서 DB/File I/O 등 동기 블로킹을 수행해서는 안 된다.</description></item>
/// </list>
/// </remarks>
public sealed class OfflineProgressionManager
{
    /// <summary>방어력 감쇠 공식에 쓰이는 기준 상수. <see cref="BattleManager"/>의 <c>DefenseConstant</c>와 동일하게
    /// 맞춰야 온라인 실시간 전투와 오프라인 수식의 기대 데미지가 어긋나지 않는다.</summary>
    private const double DefenseConstant = 100;

    /// <summary>
    /// 오프라인 경과 시간 동안 지정한 스테이지 몬스터를 상대로 발생했을 전투 결과를 계산하여 보상을 반환한다.
    /// </summary>
    /// <param name="player">보상을 지급받을 플레이어</param>
    /// <param name="stageMonster">플레이어가 방치 중이던 스테이지의 몬스터</param>
    /// <param name="offlineSeconds">오프라인 상태로 경과한 시간(초)</param>
    /// <returns>산출된 오프라인 보상 데이터</returns>
    /// <remarks>
    /// <paramref name="player"/>·<paramref name="stageMonster"/>의 <see cref="Entity.FinalStats"/>가 호출 시점 기준
    /// 최신 상태라고 가정한다(<see cref="BattleManager.CalcFinalDamage"/>와 동일한 계약). RNG에 의존하는 치명타를
    /// 매 타격마다 굴리는 대신, 크리티컬 확률을 기대값(<c>1 + CritProb × CritDmg</c>)으로 미리 반영한 평균 DPS로
    /// 치환해 결정적으로 계산한다. 이 기대 DPS 공식은 온라인 실시간 틱 시뮬레이션과 반드시 동일한 파라미터를
    /// 공유해야 정합성이 유지된다(오프라인 파밍이 온라인보다 유리/불리해지지 않도록).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="player"/> 또는 <paramref name="stageMonster"/>가 null인 경우</exception>
    public LootData ProcessOfflineTime(Player player, Monster stageMonster, int offlineSeconds)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(stageMonster);

        var attacker = player.FinalStats;
        var target = stageMonster.FinalStats;

        var defMult = DefenseConstant / (Math.Max(0, target.Def - attacker.CombatTraits.ArmorPen) + DefenseConstant);
        double effectiveDps = attacker.Atk
            * attacker.AttackScaling // 코드리뷰 F1: 무기 배율 누락으로 온라인/오프라인이 어긋나던 버그 수정
            * attacker.CombatTraits.AtkSpeed
            * (1 + attacker.CombatTraits.CritProb * attacker.CombatTraits.CritDmg)
            * defMult;

        // 코드리뷰 F2: 음수 offlineSeconds(시계 오차 등으로 유입될 수 있음)가 음수 killCount로
        // 이어지지 않도록 0으로 클램프한다.
        int safeOfflineSeconds = Math.Max(0, offlineSeconds);
        int killCount = target.MaxHp > 0 && effectiveDps > 0
            ? (int)Math.Floor(safeOfflineSeconds * effectiveDps / target.MaxHp)
            : 0;

        return stageMonster.Rewards.GenerateLoot(killCount);
    }
}
