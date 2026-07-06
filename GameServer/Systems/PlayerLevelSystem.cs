using GameServer.Entities;

namespace GameServer.Systems;

/// <summary>
/// <see cref="LevelTable"/>(마스터 데이터)을 바탕으로 플레이어의 레벨을 적용·판정하는 시스템.
/// </summary>
public static class PlayerLevelSystem
{
    /// <summary>
    /// 지정한 레벨의 스탯을 <paramref name="player"/>에 적용한다.
    /// </summary>
    /// <param name="player">적용 대상 플레이어</param>
    /// <param name="level">적용할 레벨</param>
    /// <remarks>
    /// <see cref="LevelTemplate.Hp"/>/<see cref="LevelTemplate.Atk"/>/<see cref="LevelTemplate.Def"/>를
    /// <see cref="Player.BaseStats"/>에 덮어쓴 뒤 <see cref="Entity.UpdateFinalStats"/>를 호출해
    /// <see cref="Entity.FinalStats"/>까지 갱신한다. 장비가 부여하는 모디파이어는
    /// <see cref="Player.GetExtraModifiers"/>를 통해 별도로 가산되므로 이 호출로 영향받지 않는다.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="player"/>가 null인 경우</exception>
    public static void ApplyLevel(Player player, int level)
    {
        ArgumentNullException.ThrowIfNull(player);

        var template = LevelTable.GetByLevel(level);

        player.Level = template.Level;
        player.BaseStats.Hp = template.Hp;
        player.BaseStats.Atk = template.Atk;
        player.BaseStats.Def = template.Def;

        player.UpdateFinalStats();
    }

    /// <summary>
    /// <paramref name="player"/>의 누적 경험치가 다음 레벨 이상의 임계치를 넘었는지 확인하고,
    /// 넘었다면 레벨업을 적용한다.
    /// </summary>
    /// <param name="player">판정 대상 플레이어</param>
    /// <returns>레벨업이 한 번이라도 발생했으면 true</returns>
    /// <remarks>
    /// 한 번의 호출로 여러 레벨을 한꺼번에 넘어야 하는 경우(한 번에 큰 경험치를 획득한 경우 등)도
    /// 최고 자격 레벨까지 반복 적용한다. <see cref="LevelTable.MaxLevel"/>에 도달하면 더 이상
    /// 진행하지 않는다(초과 경험치는 <see cref="Player.CurrentExp"/>에 그대로 누적된 채 남는다).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="player"/>가 null인 경우</exception>
    public static bool CheckLevelUp(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        var leveledUp = false;

        while (player.Level < LevelTable.MaxLevel)
        {
            var nextTemplate = LevelTable.GetByLevel(player.Level + 1);
            if (player.CurrentExp < nextTemplate.RequiredExp)
            {
                break;
            }

            ApplyLevel(player, nextTemplate.Level);
            leveledUp = true;
        }

        return leveledUp;
    }
}
