using System.Diagnostics;
using LoadTester.Coordination;
using LoadTester.Metrics;
using LoadTester.Options;

namespace LoadTester.Stress.Scenarios;

/// <summary>
/// 버스트/churn 스트레스가 공유하는 워커 프로세스 함대 드라이버입니다. K개 워커를 스폰해 stdout의
/// <c>@interval</c> 라인을 <see cref="CombinedAggregator"/>로 합산하고, 스냅샷·정리를 제공합니다.
/// 페이즈 진행은 상위 StressRunner가 담당하므로 <see cref="CoordinatorRunner"/>는 쓰지 않고 그 빌딩블록만 재사용합니다.
/// </summary>
/// <remarks><b>[Thread Safety:]</b> Snapshot은 thread-safe(aggregator). Start/Stop은 러너가 순차 호출.</remarks>
public sealed class WorkerFleet
{
    private readonly LoadTestOptions _workerOptions;
    private readonly int _workers;
    private readonly int _target;
    private readonly string _outRoot;
    private readonly CombinedAggregator _aggregator;
    private readonly List<Process> _processes = new();
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private volatile bool _stopped;

    /// <summary>함대를 생성합니다.</summary>
    /// <param name="workerOptions">워커 인자 원본(클라이언트 총수·ramp·포트·churn 등).</param>
    /// <param name="workers">워커 프로세스 수.</param>
    /// <param name="outRoot">워커별 출력 루트.</param>
    public WorkerFleet(LoadTestOptions workerOptions, int workers, string outRoot)
    {
        _workerOptions = workerOptions;
        _workers = workers;
        _target = workerOptions.Clients;
        _outRoot = outRoot;
        // telemetry=null, 전용 리소스 모니터(자기 자신만) — 서버 텔레메트리·리소스는 StressRunner가 별도 샘플링.
        _aggregator = new CombinedAggregator(workers, _target, telemetry: null, resources: new ResourceMonitor(null, null));
    }

    /// <summary>워커를 스폰하고 stdout 파이프를 집계기에 연결합니다.</summary>
    public void Start()
    {
        for (int i = 0; i < _workers; i++)
        {
            (int count, int offset) = WorkerShard.ForWorker(_workerOptions.Clients, _workers, i);
            Process p = WorkerProcessLauncher.Launch(_workerOptions, i, count, offset, _outRoot);
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                _aggregator.OfferLine(e.Data); // @interval/@final만 흡수, 사람용 로그는 버린다(스트레스 콘솔은 별도)
            };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _processes.Add(p);
        }
    }

    /// <summary>합산된 워커 상태를 스트레스 드라이버 스냅샷으로 반환합니다.</summary>
    public StressDriverSnapshot Snapshot()
    {
        // 종료 후에는 집계기의 마지막 @interval이 stale하므로 활성=0으로 보고한다(회복 구간 오표시 방지).
        if (_stopped)
            return default;
        CombinedInterval c = _aggregator.BuildCombined(_elapsed.Elapsed.TotalSeconds);
        return new StressDriverSnapshot(
            StressConnectAttempts: c.ConnectAttempts, StressConnectFailures: c.TotalFailures,
            StressActive: c.Active, StressReconnects: c.Reconnects,
            MalformedFramesSent: 0, StalledHeld: 0);
    }

    /// <summary>워커 프로세스를 강제 종료합니다(대량 FIN/RST → 서버 드레인 유발).</summary>
    public async Task StopAsync()
    {
        _stopped = true;
        foreach (Process p in _processes)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (Exception) { /* 이미 종료 */ }
        }
        // 종료 완료를 짧게 대기(핸들 정리).
        foreach (Process p in _processes)
        {
            try { await p.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { /* 무시 */ }
        }
    }
}
