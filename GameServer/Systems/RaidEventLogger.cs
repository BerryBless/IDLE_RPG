namespace GameServer.Systems;

/// <summary><see cref="RaidStepResult"/>를 콘솔 로그 문자열로 변환한다.</summary>
/// <remarks>
/// <b>Thread Safety:</b> Thread-safe. 공유 가변 상태가 없는 순수 함수다.
/// <b>Memory Allocation:</b> 호출마다 보간 문자열 1개를 새로 할당한다(<see cref="RaidEventType.None"/>/
/// <see cref="RaidEventType.BossDamaged"/>는 <see cref="string.Empty"/>를 반환해 할당 없음).
/// <b>Blocking 여부:</b> 즉시 반환(동기, non-blocking). I/O 없음(콘솔 출력은 호출 측 책임).
/// </remarks>
public static class RaidEventLogger
{
    /// <summary>레이드 이벤트를 한 줄 로그로 포맷한다.</summary>
    /// <param name="boss">이벤트가 발생한 레이드 보스</param>
    /// <param name="step">포맷할 레이드 스텝 결과</param>
    /// <returns><see cref="RaidEventType.BossDefeated"/>/<see cref="RaidEventType.RaidFailed"/>는
    /// 한 줄 로그 문자열, 그 외는 <see cref="string.Empty"/>.</returns>
    public static string Format(Entities.Monster boss, RaidStepResult step) => step.Event switch
    {
        RaidEventType.BossDefeated =>
            $"[레이드] 보스({boss.MonsterId}) 처치! 기여자 {step.Grants.Count}명에게 보상 분배 → 즉시 재등장",
        RaidEventType.RaidFailed =>
            "[레이드] 제한시간 초과 — 보스 HP 리셋, 기여도 초기화, 보상 없음",
        _ => string.Empty
    };
}
