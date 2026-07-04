using GameServer.Stats;

namespace GameServer.Items;

/// <summary>
/// 착용 가능하여 스탯 수정치를 부여하는 아이템의 추상 기반 타입 (무기·방어구·장신구 공통).
/// </summary>
public abstract class Equipment : Item
{
    /// <summary>아이템 등급/템플릿에 의해 고정된 기본 수정치 목록.</summary>
    public List<StatModifier> BaseModifiers { get; init; } = new();

    /// <summary>아이템 생성(강화/제작) 시 랜덤으로 부여된 수정치 목록.</summary>
    public List<StatModifier> RandomModifiers { get; init; } = new();
    
    // 두 스탯을 합친 결과를 캐싱할 내부 필드
    private List<StatModifier>? _cachedModifiers;

    /// <summary>
    /// 기본 수정치(Base)와 랜덤 수정치(Random)가 모두 포함된 전체 스탯 수정치 목록.
    /// </summary>
    /// <remarks>
    /// 프로퍼티 최초 접근 시 한 번만 리스트를 병합하여 캐싱합니다.
    /// 이후 호출에서는 추가적인 메모리 할당(GC)이 발생하지 않아 서버 성능에 유리합니다.
    /// 외부에서 임의로 수정할 수 없도록 IReadOnlyList를 반환합니다.
    /// </remarks>
    public IReadOnlyList<StatModifier> Modifiers 
    {
        get 
        {
            // 아직 캐싱되지 않았다면 (최초 1회 실행)
            if (_cachedModifiers == null)
            {
                // 두 리스트의 크기를 합친 만큼 용량(Capacity)을 미리 할당해 메모리 재할당을 방지
                _cachedModifiers = new List<StatModifier>(BaseModifiers.Count + RandomModifiers.Count);
                
                _cachedModifiers.AddRange(BaseModifiers);
                _cachedModifiers.AddRange(RandomModifiers);
            }
            
            return _cachedModifiers;
        }
    }
}
