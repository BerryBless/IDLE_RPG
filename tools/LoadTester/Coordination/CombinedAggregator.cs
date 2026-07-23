using System.Collections.Concurrent;
using LoadTester.Metrics;
using LoadTester.Telemetry;

namespace LoadTester.Coordination;

/// <summary>
/// 전 워커의 최신 <see cref="WorkerStatus"/>와 워커 <see cref="WorkerFinal"/>을 스레드 안전하게 모아,
/// 요청 시 서버측 관측(텔레메트리·리소스)과 합쳐 <see cref="CombinedInterval"/> 1건을 만듭니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 워커별 stdout 핸들러(여러 스레드)가
/// <see cref="OfferLine"/>로 기록하고, 코디네이터 집계 루프(단일 스레드)가 <see cref="BuildCombined"/>로
/// 읽는다. ConcurrentDictionary로 경합을 흡수한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 워커당 최신값 1개만 유지(맵 크기 = 워커 수).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking.</description></item>
/// </list>
/// </remarks>
public sealed class CombinedAggregator
{
    // ConcurrentDictionary: 워커별 stdout 콜백(멀티 스레드 생산자) → 집계 루프(단일 소비자) 경로에서
    // 워커 인덱스별 최신 상태를 락 없이 교체한다.
    private readonly ConcurrentDictionary<int, WorkerStatus> _latestStatus = new();
    private readonly ConcurrentDictionary<int, WorkerFinal> _finals = new();
    private readonly ConcurrentDictionary<int, bool> _everReported = new();

    private readonly int _totalWorkers;
    private readonly int _target;
    private readonly TelemetrySubscriber? _telemetry;
    private readonly ResourceMonitor _resources;

    /// <summary>집계기를 생성합니다.</summary>
    /// <param name="totalWorkers">전체 워커 수.</param>
    /// <param name="target">목표 동시 연결 수(전체).</param>
    /// <param name="telemetry">서버 텔레메트리 구독(없으면 null).</param>
    /// <param name="resources">서버·자기 리소스 모니터.</param>
    public CombinedAggregator(int totalWorkers, int target, TelemetrySubscriber? telemetry, ResourceMonitor resources)
    {
        _totalWorkers = totalWorkers;
        _target = target;
        _telemetry = telemetry;
        _resources = resources;
    }

    /// <summary>워커가 이미 종료 요약(@final)을 보냈는지.</summary>
    public bool HasFinal(int workerIndex) => _finals.ContainsKey(workerIndex);

    /// <summary>@final을 보낸 워커 수.</summary>
    public int FinalCount => _finals.Count;

    /// <summary>@interval을 한 번이라도 보낸 워커 수.</summary>
    public int ReportingWorkerCount => _everReported.Count;

    /// <summary>한 워커 stdout 라인을 제공합니다. <c>@interval</c>/<c>@final</c>이면 흡수하고 true, 아니면 false(사람용 로그).</summary>
    public bool OfferLine(string line)
    {
        if (WorkerLineProtocol.TryParseInterval(line, out WorkerStatus? status) && status is not null)
        {
            _latestStatus[status.WorkerIndex] = status;
            _everReported[status.WorkerIndex] = true;
            return true;
        }
        if (WorkerLineProtocol.TryParseFinal(line, out WorkerFinal? final) && final is not null)
        {
            _finals[final.WorkerIndex] = final;
            _everReported[final.WorkerIndex] = true;
            // 종료한 워커의 라이브 상태를 제거한다: 남겨두면 종료 후 집계에서 그 워커의 마지막 active/auth
            // (예: 400)가 유효한 것처럼 합산돼, 서버 텔레메트리가 0으로 떨어진 것과 섞여 "세션 유실"
            // 오탐을 만든다. 제거하면 종료 진행에 따라 active가 자연히 0으로 수렴한다.
            _latestStatus.TryRemove(final.WorkerIndex, out _);
            return true;
        }
        return false;
    }

    /// <summary>현재 워커 상태 + 서버측 관측을 합쳐 구간 집계를 만듭니다(집계 루프 전용).</summary>
    /// <param name="elapsedSeconds">코디네이터 경과 초.</param>
    public CombinedInterval BuildCombined(double elapsedSeconds)
    {
        int active = 0, authenticated = 0, stalled = 0, reporting = 0;
        long attempts = 0, failures = 0, disconnects = 0, reconnects = 0;
        double maxWorkerWs = 0;
        foreach (WorkerStatus s in _latestStatus.Values)
        {
            reporting++;
            active += s.Active;
            authenticated += s.Authenticated;
            stalled += s.StalledClients;
            attempts += s.ConnectAttempts;
            failures += s.TotalFailures;
            disconnects += s.UnexpectedDisconnects;
            reconnects += s.Reconnects;
            maxWorkerWs = Math.Max(maxWorkerWs, s.SelfWorkingSetMb);
        }

        TelemetrySample? tele = _telemetry?.Latest;
        ResourceSample res = _resources.Sample();

        return new CombinedInterval(
            ElapsedSeconds: elapsedSeconds,
            WorkersReporting: reporting, TotalWorkers: _totalWorkers,
            Active: active, Target: _target, Authenticated: authenticated,
            ConnectAttempts: attempts, TotalFailures: failures,
            UnexpectedDisconnects: disconnects, Reconnects: reconnects,
            StalledClients: stalled, MaxWorkerWorkingSetMb: maxWorkerWs,
            TeleConnected: tele?.ConnectedCount, TeleRejected: tele?.RejectedConnections,
            ServerWorkingSetMb: res.ServerWorkingSetMb, ServerCpuPercent: res.ServerCpuPercent,
            ServerProcessLost: res.ServerProcessLost);
    }
}
