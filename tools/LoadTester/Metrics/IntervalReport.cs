namespace LoadTester.Metrics;

/// <summary>
/// 샘플러가 리포트 주기마다 생성하는 구간 관측 결과입니다. 콘솔·NDJSON·판정기가 공유하는
/// 단일 데이터 계약으로, 생성 후 불변입니다.
/// </summary>
/// <param name="ElapsedSeconds">실행 시작 이후 경과 초.</param>
/// <param name="Active">현재 소켓 연결 상태인 클라이언트 수.</param>
/// <param name="Target">목표 클라이언트 수(--clients).</param>
/// <param name="Authenticated">현재 인증 완료 상태인 클라이언트 수.</param>
/// <param name="Totals">누적 카운터 스냅샷.</param>
/// <param name="BroadcastsDelta">이번 구간 수신 브로드캐스트 수.</param>
/// <param name="BytesInDelta">이번 구간 수신 바이트.</param>
/// <param name="StalledClients">스톨 상태(연결·인증됐으나 수신 정지) 클라이언트 수.</param>
/// <param name="FullStall">연결된 전원이 스톨이고 서버는 접속자가 있다고 보고하는 상태(판정 후보).</param>
/// <param name="RttP50Ms">이번 구간 RTT p50(ms).</param>
/// <param name="RttP95Ms">이번 구간 RTT p95(ms).</param>
/// <param name="RttP99Ms">이번 구간 RTT p99(ms).</param>
/// <param name="CumRttP50Ms">누적 RTT p50(ms).</param>
/// <param name="CumRttP95Ms">누적 RTT p95(ms).</param>
/// <param name="CumRttP99Ms">누적 RTT p99(ms).</param>
/// <param name="TeleConnected">서버 텔레메트리 보고 접속 수(미수신 시 null).</param>
/// <param name="TeleRejected">서버 텔레메트리 누적 거부 수(미수신 시 null).</param>
/// <param name="TeleGeneration">서버 레이드 세대(미수신 시 null).</param>
/// <param name="TeleBossHpPct">보스 HP 백분율(미수신 시 null).</param>
/// <param name="Resource">리소스 샘플(서버 + 자기 자신).</param>
public sealed record IntervalReport(
    double ElapsedSeconds,
    int Active, int Target, int Authenticated,
    CounterTotals Totals,
    long BroadcastsDelta, long BytesInDelta,
    int StalledClients, bool FullStall,
    double RttP50Ms, double RttP95Ms, double RttP99Ms,
    double CumRttP50Ms, double CumRttP95Ms, double CumRttP99Ms,
    int? TeleConnected, long? TeleRejected, int? TeleGeneration, double? TeleBossHpPct,
    ResourceSample Resource);
