using GameServer.Stats;

namespace GameServer.Combat;

/// <summary>
/// 버프 또는 디버프로 작용하는 시간 제한형 전투 상태 효과.
/// <see cref="BuffManager"/>에 의해 소유되며 매 틱마다 잔여 시간이 감소한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> <see cref="Tick"/>은 전투 갱신 루프(단일 스레드)에서만 호출되는 것을 전제로 한다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="GetModifiers"/> 호출마다 새 리스트를 할당하지 않도록 구현 시 내부 캐시 재사용을 권장한다.</description></item>
/// </list>
/// </remarks>
public sealed class StatusEffect
{
    /// <summary>효과를 식별하는 고유 문자열 ID (효과 정의 테이블과 매칭).</summary>
    public string EffectId { get; init; } = string.Empty;

    /// <summary>효과의 총 지속 시간(초).</summary>
    public float MaxDuration { get; set; }

    /// <summary>남은 지속 시간(초).</summary>
    public float TimeRemaining { get; set; }

    /// <summary>디버프 여부 (false면 버프).</summary>
    public bool IsDebuff { get; init; }

    /// <summary>경과 시간만큼 <see cref="TimeRemaining"/>을 감소시킨다.</summary>
    /// <param name="deltaTime">이전 갱신 이후 경과한 시간(초)</param>
    public void Tick(float deltaTime) => throw new NotImplementedException();

    /// <summary>효과가 만료되었는지 여부를 반환한다.</summary>
    /// <returns><see cref="TimeRemaining"/>이 0 이하이면 true</returns>
    public bool IsExpired() => throw new NotImplementedException();

    /// <summary>이 효과가 부여하는 스탯 수정치 목록을 반환한다.</summary>
    /// <returns>적용할 <see cref="StatModifier"/> 목록</returns>
    public List<StatModifier> GetModifiers() => throw new NotImplementedException();
}
