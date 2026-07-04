using GameServer.Stats;

namespace GameServer.Combat;

/// <summary>
/// 엔티티에 부여된 모든 <see cref="StatusEffect"/>(버프/디버프)의 생명주기를 관리하고,
/// 활성 효과들의 스탯 수정치를 집계하여 제공한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> <see cref="Update"/>는 전투 갱신 루프(단일 스레드)에서 호출되는 것을 전제로 한다. Not Thread-safe.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="GetAllActiveModifiers"/>는 매 호출마다 집계 리스트를 새로 구성할 수 있어 hot path에서는 결과 캐싱을 고려해야 한다.</description></item>
/// </list>
/// </remarks>
public sealed class BuffManager
{
    /// <summary>현재 활성화된 상태 효과 목록.</summary>
    private readonly List<StatusEffect> _activeEffects = new();

    /// <summary>새 상태 효과를 부여한다.</summary>
    /// <param name="effect">부여할 효과</param>
    public void ApplyEffect(StatusEffect effect) => throw new NotImplementedException();

    /// <summary>지정한 상태 효과를 즉시 제거한다.</summary>
    /// <param name="effect">제거할 효과</param>
    public void RemoveEffect(StatusEffect effect) => throw new NotImplementedException();

    /// <summary>경과 시간만큼 모든 활성 효과를 갱신하고 만료된 효과를 제거한다.</summary>
    /// <param name="deltaTime">이전 갱신 이후 경과한 시간(초)</param>
    public void Update(float deltaTime) => throw new NotImplementedException();

    /// <summary>현재 활성화된 모든 효과의 스탯 수정치를 합쳐 반환한다.</summary>
    /// <returns>활성 효과들이 부여하는 <see cref="StatModifier"/> 전체 목록</returns>
    public List<StatModifier> GetAllActiveModifiers() => throw new NotImplementedException();
}
