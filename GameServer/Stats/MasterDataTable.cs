namespace GameServer.Stats;

/// <summary>
/// <see cref="IMasterDataTable{TKey,T}"/>를 구현하는 마스터 데이터 테이블들의 공통 기반. 생성자에서
/// <see cref="Dictionary{TKey,TValue}"/> 인덱스를 한 번만 구축해 <see cref="GetById"/>를 O(1)로 만든다.
/// </summary>
/// <typeparam name="TKey">조회 키 타입(예: ID, 레벨)</typeparam>
/// <typeparam name="T">템플릿 타입</typeparam>
/// <remarks>
/// 코드리뷰 2026-07-06 Medium 수정: 이전에는 <c>MonsterTable</c>/<c>EquipmentTable</c>/<c>LevelTable</c>
/// 세 곳에 완전히 동일한 "foreach 선형 탐색 + KeyNotFoundException" 로직이 중복돼 있었다(아키텍처·
/// 스타일 리뷰 양쪽에서 지적). 이 기반 클래스로 옮겨 중복을 제거하고, 부수 효과로 두 가지를 얻는다:
/// <list type="number">
/// <item><description><b>조회 성능:</b> 생성자에서 인덱스를 1회 구축(O(n))한 뒤 <see cref="GetById"/>는
/// O(1) Dictionary 조회다. <c>LevelTable.GetById</c>는 몬스터 처치마다(<c>BattleLoop.Tick</c> →
/// <c>PlayerLevelSystem.CheckLevelUp</c>) 호출되는 핫패스라 특히 유효하다.</description></item>
/// <item><description><b>중복 키 즉시 검출:</b> <see cref="Dictionary{TKey,TValue}"/> 생성 시 키 중복이
/// 있으면 <see cref="ArgumentException"/>이 즉시 발생한다. 마스터 데이터에 같은 ID가 두 번 들어가는
/// 실수(하드코딩이든 향후 JSON 로딩이든)를 조용히 넘기지 않고 테이블 생성 시점에 바로 실패시킨다.</description></item>
/// </list>
/// </remarks>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 생성 후에는 불변(모든 필드가 생성자에서만 설정)이므로
/// 여러 스레드가 동시에 <see cref="All"/>/<see cref="GetById"/>를 호출해도 안전하다. 생성자 자체의
/// 동시 호출(같은 인스턴스를 여러 스레드가 동시에 생성하는 상황)은 지원 대상이 아니다 — 통상
/// 애플리케이션 시작 시 한 번만 생성한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 생성자에서 <see cref="Dictionary{TKey,TValue}"/> 1개를
/// 템플릿 개수만큼 할당한다. 이후 <see cref="GetById"/>/<see cref="All"/> 호출은 추가 힙 할당이 없다.</description></item>
/// <item><description><b>Blocking 여부:</b> 전부 즉시 반환(동기, non-blocking). I/O 없음.</description></item>
/// </list>
/// </remarks>
public abstract class MasterDataTable<TKey, T> : IMasterDataTable<TKey, T> where TKey : notnull
{
    private readonly Dictionary<TKey, T> _byKey;
    private readonly string _entityLabel;

    /// <summary>등록된 전체 템플릿 목록(생성자에 전달된 순서 그대로).</summary>
    public IReadOnlyList<T> All { get; }

    /// <summary>주어진 템플릿 목록과 키 선택 함수로 테이블을 구성한다.</summary>
    /// <param name="templates">등록할 템플릿 목록</param>
    /// <param name="keySelector">각 템플릿에서 조회 키를 추출하는 함수</param>
    /// <param name="entityLabel">이 테이블이 다루는 대상의 한글 표시명(예외 메시지에 사용, 예: "몬스터 템플릿")</param>
    /// <exception cref="ArgumentNullException"><paramref name="templates"/> 또는 <paramref name="keySelector"/>가 null인 경우</exception>
    /// <exception cref="ArgumentException"><paramref name="templates"/>에 <paramref name="keySelector"/> 기준
    /// 중복 키가 있는 경우 — 마스터 데이터 설정 오류를 조용히 넘기지 않고 테이블 생성 시점에 즉시 실패시킨다.</exception>
    protected MasterDataTable(IReadOnlyList<T> templates, Func<T, TKey> keySelector, string entityLabel)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(keySelector);

        All = templates;
        _entityLabel = entityLabel;
        _byKey = templates.ToDictionary(keySelector);
    }

    /// <summary>지정한 키의 템플릿을 찾는다.</summary>
    /// <param name="id">조회 키</param>
    /// <returns>일치하는 템플릿</returns>
    /// <exception cref="KeyNotFoundException">일치하는 템플릿이 없는 경우 — 마스터 데이터 설정
    /// 오류를 조용히 넘기지 않고 즉시 실패시킨다.</exception>
    public T GetById(TKey id)
    {
        if (_byKey.TryGetValue(id, out var template))
        {
            return template;
        }

        throw new KeyNotFoundException($"{id}에 해당하는 {_entityLabel}을(를) 찾을 수 없습니다.");
    }
}
