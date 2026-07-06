namespace GameServer.Stats;

/// <summary>
/// 키로 조회 가능한 마스터 데이터 테이블의 공통 계약.
/// </summary>
/// <typeparam name="TKey">조회 키 타입(예: ID, 레벨)</typeparam>
/// <typeparam name="T">템플릿 타입</typeparam>
/// <remarks>
/// 코드리뷰(2026-07-06) H1 수정: 이전에는 <c>MonsterTable</c>/<c>EquipmentTable</c>/<c>LevelTable</c>이
/// 모두 <c>static class</c>였고 데이터가 정적 필드 초기화식에 고정되어 있었다. 이는 향후 JSON 파일
/// 기반 로딩으로 이관할 때 파일 I/O가 정적 생성자에 들어가 예외가 <see cref="TypeInitializationException"/>으로
/// 래핑되고, 테스트에서 대체 데이터셋을 주입할 수 없으며, 소비자가 구체 static 타입에 하드 바인딩되어
/// DIP를 위반하는 문제가 있었다. 이제 각 테이블은 이 인터페이스를 구현하는 일반 인스턴스이고,
/// 하드코딩 데이터는 <c>CreateDefault()</c> 정적 팩토리로만 생성되며(정적 생성자가 아니라 일반
/// 메서드이므로 예외가 그대로 전파됨), 나중에 JSON 기반 구현체를 별도로 만들어 갈아 끼울 수 있다.
/// <b>배치 위치:</b> Stats는 프로젝트 의존 관계상 "기반, 무의존" 계층이라(<c>plan/gameserver_domain_scaffold_0704.md</c>
/// §3 참고), Items/Systems 양쪽이 순환 의존 없이 이 인터페이스를 구현할 수 있도록 여기에 둔다.
/// </remarks>
public interface IMasterDataTable<in TKey, out T>
{
    /// <summary>등록된 전체 템플릿 목록.</summary>
    IReadOnlyList<T> All { get; }

    /// <summary>지정한 키의 템플릿을 찾는다.</summary>
    /// <param name="id">조회 키</param>
    /// <returns>일치하는 템플릿</returns>
    /// <exception cref="KeyNotFoundException">일치하는 템플릿이 없는 경우</exception>
    T GetById(TKey id);
}
