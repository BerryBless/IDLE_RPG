using LoadTester.Metrics;

namespace LoadTester.Stress;

/// <summary>
/// 스트레스 샘플러가 주기마다 만드는 구간 관측입니다. 콘솔·NDJSON·판정기가 공유하는 불변 계약입니다.
/// </summary>
/// <param name="Phase">이 구간의 페이즈.</param>
/// <param name="ElapsedSeconds">실행 시작 이후 경과 초.</param>
/// <param name="Probe">정상 대조군 건강도.</param>
/// <param name="Driver">스트레스 드라이버 상태.</param>
/// <param name="TeleConnected">서버 텔레메트리 접속 수(미수신 시 null).</param>
/// <param name="TeleRejected">서버 누적 거부 수(미수신 시 null).</param>
/// <param name="Resource">서버·자기 리소스 샘플.</param>
public sealed record StressIntervalReport(
    StressPhase Phase,
    double ElapsedSeconds,
    ProbeHealthSnapshot Probe,
    StressDriverSnapshot Driver,
    int? TeleConnected,
    long? TeleRejected,
    ResourceSample Resource);
