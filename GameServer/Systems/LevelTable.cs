namespace GameServer.Systems;

/// <summary>
/// 플레이어 레벨 마스터 데이터 테이블. 현재는 1~10레벨을 C#으로 하드코딩해 보관한다.
/// </summary>
/// <remarks>
/// <b>[JSON 이관 대비]</b> <see cref="All"/>의 초기화 리스트는 이 파일에만 격리되어 있다(<see cref="MonsterTable"/>/
/// <see cref="Items.EquipmentTable"/>과 동일한 패턴). 나중에 JSON 파일 기반으로 옮길 때는 이
/// 프로퍼티의 초기화식만 교체하면 되고, <see cref="LevelTemplate"/>·<see cref="GetByLevel"/>을
/// 사용하는 다른 코드는 전혀 바뀔 필요가 없다.
/// </remarks>
public static class LevelTable
{
    /// <summary>등록된 전체 레벨 목록(1레벨부터 오름차순).</summary>
    public static IReadOnlyList<LevelTemplate> All { get; } = new List<LevelTemplate>
    {
        new() { Level = 1, RequiredExp = 0, Hp = 100, Atk = 10, Def = 2 },
        new() { Level = 2, RequiredExp = 20, Hp = 130, Atk = 14, Def = 3 },
        new() { Level = 3, RequiredExp = 50, Hp = 170, Atk = 19, Def = 5 },
        new() { Level = 4, RequiredExp = 100, Hp = 220, Atk = 25, Def = 7 },
        new() { Level = 5, RequiredExp = 180, Hp = 280, Atk = 32, Def = 10 },
        new() { Level = 6, RequiredExp = 300, Hp = 350, Atk = 40, Def = 14 },
        new() { Level = 7, RequiredExp = 470, Hp = 430, Atk = 50, Def = 19 },
        new() { Level = 8, RequiredExp = 700, Hp = 520, Atk = 62, Def = 25 },
        new() { Level = 9, RequiredExp = 1000, Hp = 620, Atk = 76, Def = 32 },
        new() { Level = 10, RequiredExp = 1400, Hp = 730, Atk = 92, Def = 40 }
    };

    /// <summary>테이블에 정의된 최고 레벨.</summary>
    public static int MaxLevel => All.Count;

    /// <summary>지정한 레벨의 템플릿을 찾는다.</summary>
    /// <param name="level">조회할 레벨</param>
    /// <returns>일치하는 <see cref="LevelTemplate"/></returns>
    /// <exception cref="KeyNotFoundException">일치하는 레벨이 없는 경우 — 마스터 데이터 설정
    /// 오류를 조용히 넘기지 않고 즉시 실패시킨다.</exception>
    public static LevelTemplate GetByLevel(int level)
    {
        foreach (var template in All)
        {
            if (template.Level == level)
            {
                return template;
            }
        }

        throw new KeyNotFoundException($"Level {level}에 해당하는 레벨 템플릿을 찾을 수 없습니다.");
    }
}
