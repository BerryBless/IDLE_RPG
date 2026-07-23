namespace LoadTester.Stress;

/// <summary>스트레스 시나리오 종류.</summary>
public enum StressScenarioKind
{
    /// <summary>스파이크/버스트 과부하 → 해제 → 회복.</summary>
    Burst,

    /// <summary>연결 churn(접속→인증→즉시 종료→재접속 폭주).</summary>
    Churn,

    /// <summary>비정상/악의적 패킷 플러드.</summary>
    Malformed,

    /// <summary>Slowloris/정체 피어(연결만 하고 인증 안 함 또는 느린 드립).</summary>
    Slowloris,
}

/// <summary>시나리오 실행 모델.</summary>
public enum StressExecutionModel
{
    /// <summary>제어 프로세스 안에서 직접 클라이언트를 구동(수천 규모, 강건성 측정).</summary>
    InProcess,

    /// <summary>워커 프로세스를 스폰해 대규모 부하(과부하/고속 churn).</summary>
    MultiProcess,
}

/// <summary>
/// 시나리오별 회복 기대치입니다. 판정기가 어떤 회복 규칙을 <b>게이트</b>로 적용할지 결정합니다.
/// slowloris/malformed는 누적이 예상 현상이라 회복을 <b>리포트만</b> 하고 게이트하지 않습니다
/// (게이트하면 정상적으로 생존한 서버가 오히려 FAIL이 되기 때문).
/// </summary>
/// <param name="ExpectSessionCountRecovery">해제 후 서버 세션 수가 기준선으로 복귀할 것으로 기대하는지.</param>
/// <param name="ExpectServerWsRecovery">해제 후 서버 워킹셋이 기준선으로 복귀할 것으로 기대하는지.</param>
/// <param name="HeadlineFinding">이 시나리오가 드러낼 핵심 발견(리포트 헤드라인).</param>
public readonly record struct StressExpectations(
    bool ExpectSessionCountRecovery, bool ExpectServerWsRecovery, string HeadlineFinding);

/// <summary>시나리오 드라이버의 현재 상태 스냅샷(샘플러가 주기적으로 읽음).</summary>
/// <param name="StressConnectAttempts">스트레스 클라이언트 연결 시도 누적.</param>
/// <param name="StressConnectFailures">스트레스 클라이언트 연결 실패 누적.</param>
/// <param name="StressActive">현재 살아있는 스트레스 연결 수.</param>
/// <param name="StressReconnects">churn 재접속 누적(churn 처리율 산출).</param>
/// <param name="MalformedFramesSent">전송한 악성 프레임 누적.</param>
/// <param name="StalledHeld">현재 붙잡고 있는 정체 피어 수.</param>
public readonly record struct StressDriverSnapshot(
    long StressConnectAttempts, long StressConnectFailures, long StressActive,
    long StressReconnects, long MalformedFramesSent, long StalledHeld);

/// <summary>
/// 스트레스 시나리오 드라이버 추상화입니다. StressRunner가 During 페이즈에서 <see cref="DriveAsync"/>로
/// 구동하고, Release 페이즈에서 <see cref="ReleaseAsync"/>로 정리하며, 샘플러가 <see cref="Snapshot"/>으로
/// 진행을 관측합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> <see cref="Snapshot"/>은 샘플러 스레드에서 동시 호출되므로 thread-safe해야 함
/// (내부 카운터는 Interlocked/Volatile). <see cref="DriveAsync"/>/<see cref="ReleaseAsync"/>는 러너가 순차 호출.
/// <b>[Blocking:]</b> Non-blocking(비동기).
/// </remarks>
public interface IStressScenario
{
    /// <summary>시나리오 종류.</summary>
    StressScenarioKind Kind { get; }

    /// <summary>실행 모델.</summary>
    StressExecutionModel Model { get; }

    /// <summary>회복 기대치(판정 게이트 결정).</summary>
    StressExpectations Expectations { get; }

    /// <summary>During 페이즈에서 스트레스를 구동합니다. <paramref name="stressToken"/> 취소 시 반환.</summary>
    Task DriveAsync(StressRunContext context, CancellationToken stressToken);

    /// <summary>스트레스를 해제·정리합니다(워커 kill / 풀 종료).</summary>
    Task ReleaseAsync();

    /// <summary>현재 드라이버 상태 스냅샷(무할당, thread-safe).</summary>
    StressDriverSnapshot Snapshot();
}
