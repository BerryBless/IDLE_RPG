using GameServer.Entities;
using GameServer.Stats;

namespace GameServer.Systems;

/// <summary>
/// 레벨 마스터 데이터(<see cref="IMasterDataTable{TKey,T}"/>)를 바탕으로 플레이어의 레벨을 적용·판정하는 시스템.
/// </summary>
/// <remarks>
/// 코드리뷰 2026-07-06 H1 수정: 이전에는 static class가 static <c>LevelTable</c>을 직접 호출했으나,
/// 이제 인스턴스가 생성자로 <see cref="IMasterDataTable{TKey,T}"/>를 주입받는다. 레벨 규칙을 다른
/// 데이터셋으로 바꾸거나 테스트에서 대체 테이블을 주입할 수 있다.
/// </remarks>
public sealed class PlayerLevelSystem
{
    private readonly IMasterDataTable<int, LevelTemplate> _levelTable;

    /// <summary>지정한 레벨 테이블을 사용하는 시스템을 생성한다.</summary>
    /// <param name="levelTable">레벨 마스터 데이터 테이블</param>
    public PlayerLevelSystem(IMasterDataTable<int, LevelTemplate> levelTable)
    {
        ArgumentNullException.ThrowIfNull(levelTable);
        _levelTable = levelTable;
    }

    /// <summary>하드코딩된 기본 1~10레벨 데이터(<see cref="LevelTable.CreateDefault"/>)를 사용하는 시스템을 생성한다.</summary>
    public static PlayerLevelSystem CreateDefault() => new(LevelTable.CreateDefault());

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
    public void ApplyLevel(Player player, int level)
    {
        ArgumentNullException.ThrowIfNull(player);

        var template = _levelTable.GetById(level);

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
    /// 최고 자격 레벨까지 반복 적용한다. 테이블에 정의된 최고 레벨에 도달하면 더 이상 진행하지
    /// 않는다(초과 경험치는 <see cref="Player.CurrentExp"/>에 그대로 누적된 채 남는다).
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="player"/>가 null인 경우</exception>
    public bool CheckLevelUp(Player player)
    {
        ArgumentNullException.ThrowIfNull(player);

        var leveledUp = false;
        var maxLevel = _levelTable.All.Count == 0 ? 0 : _levelTable.All.Max(t => t.Level);

        while (player.Level < maxLevel)
        {
            var nextTemplate = _levelTable.GetById(player.Level + 1);
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
