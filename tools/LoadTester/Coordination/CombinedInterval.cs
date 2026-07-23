namespace LoadTester.Coordination;

/// <summary>
/// 코디네이터가 전 워커의 최신 <see cref="WorkerStatus"/>를 합산하고 서버측 관측(텔레메트리·리소스)을
/// 더해 만든 구간 집계입니다. 콘솔·통합 NDJSON·용량 판정기가 공유하는 불변 데이터 계약입니다.
/// </summary>
/// <param name="ElapsedSeconds">코디네이터 실행 경과 초.</param>
/// <param name="WorkersReporting">이번 구간에 보고한 워커 수.</param>
/// <param name="TotalWorkers">전체 워커 수.</param>
/// <param name="Active">전 워커 현재 연결 수 합.</param>
/// <param name="Target">목표 동시 연결 수(전체).</param>
/// <param name="Authenticated">전 워커 현재 인증 수 합.</param>
/// <param name="ConnectAttempts">전 워커 누적 연결 시도 합.</param>
/// <param name="TotalFailures">전 워커 누적 실패 합.</param>
/// <param name="UnexpectedDisconnects">전 워커 누적 예기치 않은 끊김 합.</param>
/// <param name="Reconnects">전 워커 누적 재접속 합.</param>
/// <param name="StalledClients">전 워커 현재 스톨 수 합.</param>
/// <param name="MaxWorkerWorkingSetMb">이번 구간 워커 중 최대 워킹셋(MB).</param>
/// <param name="TeleConnected">서버 텔레메트리 접속 수(미수신 시 null).</param>
/// <param name="TeleRejected">서버 텔레메트리 누적 거부 수(미수신 시 null).</param>
/// <param name="ServerWorkingSetMb">서버 워킹셋(MB, 미측정 시 null).</param>
/// <param name="ServerCpuPercent">서버 CPU%(미측정 시 null).</param>
/// <param name="ServerProcessLost">서버 프로세스 소실 관측 여부.</param>
public sealed record CombinedInterval(
    double ElapsedSeconds,
    int WorkersReporting, int TotalWorkers,
    int Active, int Target, int Authenticated,
    long ConnectAttempts, long TotalFailures, long UnexpectedDisconnects, long Reconnects,
    int StalledClients, double MaxWorkerWorkingSetMb,
    int? TeleConnected, long? TeleRejected,
    double? ServerWorkingSetMb, double? ServerCpuPercent, bool ServerProcessLost);
