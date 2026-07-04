using GameServer.Combat;
using GameServer.Stats;

namespace GameServer.Entities;

/// <summary>
/// 전투에 참여하는 모든 대상(플레이어·몬스터)의 공통 추상 기반 타입.
/// 기본 스탯·특성·버프·최종 스탯 캐시를 소유하고 공통 갱신 파이프라인을 제공한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> <see cref="Update"/>/<see cref="UpdateFinalStats"/>는 전투 갱신 루프(단일 스레드)에서 호출되는 것을 전제로 한다.</description></item>
/// <item><description><b>Thread Safety:</b> Not Thread-safe. 동일 인스턴스에 대한 동시 갱신은 호출 측이 금지해야 한다.</description></item>
/// </list>
/// </remarks>
public abstract class Entity
{
    /// <summary>엔티티 인스턴스를 식별하는 고유 ID.</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>엔티티 레벨.</summary>
    public int Level { get; set; }

    /// <summary>장비/버프를 제외한 순수 기본 스탯.</summary>
    protected BaseStats BaseStats { get; set; } = new();

    /// <summary>장비/버프를 제외한 순수 기본 특성치.</summary>
    protected Traits BaseTraits { get; set; } = new();

    /// <summary>기본 스탯 + 모든 수정치가 반영된 최종 스탯 캐시.</summary>
    public FinalStats FinalStats { get; init; } = new();

    /// <summary>이 엔티티에 부여된 버프/디버프를 관리하는 매니저.</summary>
    public BuffManager BuffManager { get; init; } = new();

    /// <summary>지정한 양의 피해를 받아 현재 체력을 감소시킨다.</summary>
    /// <param name="amount">받을 피해량</param>
    public void TakeDamage(BigNumber amount) => throw new NotImplementedException();

    /// <summary>경과 시간만큼 이 엔티티의 전투 관련 상태(버프 등)를 갱신한다.</summary>
    /// <param name="deltaTime">이전 갱신 이후 경과한 시간(초)</param>
    public void Update(float deltaTime) => throw new NotImplementedException();

    /// <summary>
    /// <see cref="BaseStats"/>·<see cref="BaseTraits"/>·장비·버프·<see cref="GetExtraModifiers"/> 수정치를
    /// 모두 합산하여 <see cref="FinalStats"/> 캐시를 갱신한다.
    /// </summary>
    public void UpdateFinalStats() => throw new NotImplementedException();

    /// <summary>
    /// 하위 타입(플레이어의 장비, 몬스터의 어픽스 등) 고유의 추가 수정치 소스를 제공한다.
    /// </summary>
    /// <returns>하위 타입이 기여하는 <see cref="StatModifier"/> 목록</returns>
    protected abstract List<StatModifier> GetExtraModifiers();
}
