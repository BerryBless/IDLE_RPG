using GameServer.Stats;

namespace GameServer.Systems;

/// <summary>
/// 플레이어 레벨 마스터 데이터 테이블. 현재는 1~10레벨을 C#으로 하드코딩해 보관한다.
/// </summary>
/// <remarks>
/// <b>[JSON 이관 대비]</b> 하드코딩 데이터는 <see cref="CreateDefault"/> 정적 팩토리 메서드
/// 안에서만 만들어진다(정적 생성자가 아니므로 실패 시 예외가 그대로 전파되고, 호출 시점을
/// 호출 측이 제어할 수 있다). 나중에 JSON 파일 기반으로 옮길 때는 <c>LevelTable.FromJson(path)</c>
/// 같은 별도 팩토리를 추가하기만 하면 되고, <see cref="IMasterDataTable{TKey,T}"/>를 사용하는
/// 다른 코드는 전혀 바뀔 필요가 없다(코드리뷰 2026-07-06 H1 수정 — 이전에는 static class + 정적
/// 필드 초기화식으로 고정되어 있어 테스트에서 대체 데이터셋을 주입할 수 없었다).
/// </remarks>
public sealed class LevelTable : IMasterDataTable<int, LevelTemplate>
{
    /// <summary>등록된 전체 레벨 목록(1레벨부터 오름차순).</summary>
    public IReadOnlyList<LevelTemplate> All { get; }

    /// <summary>테이블에 정의된 최고 레벨.</summary>
    /// <remarks>
    /// 코드리뷰 H1과 함께 발견된 부수 결함 수정: 이전에는 <c>All.Count</c>(개수)로 계산해
    /// "레벨이 1부터 빈틈없이 연속"이라는 암묵 가정이 있었다. <c>All.Max(t => t.Level)</c>로
    /// 바꿔 데이터에 갭이 생겨도 실제 최고 레벨을 정확히 반영하도록 했다.
    /// </remarks>
    public int MaxLevel => All.Count == 0 ? 0 : All.Max(t => t.Level);

    /// <summary>주어진 템플릿 목록으로 테이블을 구성한다.</summary>
    /// <param name="templates">등록할 레벨 템플릿 목록</param>
    public LevelTable(IReadOnlyList<LevelTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        All = templates;
    }

    /// <summary>하드코딩된 기본 1~10레벨 데이터로 테이블을 생성한다.</summary>
    public static LevelTable CreateDefault() => new(BuildDefaultTemplates());

    private static List<LevelTemplate> BuildDefaultTemplates() => new()
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

    /// <summary>지정한 레벨의 템플릿을 찾는다.</summary>
    /// <param name="id">조회할 레벨</param>
    /// <returns>일치하는 <see cref="LevelTemplate"/></returns>
    /// <exception cref="KeyNotFoundException">일치하는 레벨이 없는 경우 — 마스터 데이터 설정
    /// 오류를 조용히 넘기지 않고 즉시 실패시킨다.</exception>
    public LevelTemplate GetById(int id)
    {
        foreach (var template in All)
        {
            if (template.Level == id)
            {
                return template;
            }
        }

        throw new KeyNotFoundException($"Level {id}에 해당하는 레벨 템플릿을 찾을 수 없습니다.");
    }
}
