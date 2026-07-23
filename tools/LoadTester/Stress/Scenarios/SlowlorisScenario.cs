using LoadTester.Stress.Clients;

namespace LoadTester.Stress.Scenarios;

/// <summary>
/// Slowloris/정체 피어 시나리오(인프로세스). 인증하지 않는(또는 느린 드립) 피어 풀이 서버 세션을
/// 붙잡아, 서버에 idle sweep이 배선돼 있지 않을 때 세션·워킹셋이 얼마나 누적되는지, 그리고 정상
/// 프로브가 그 와중에도 접속·인증되는지를 측정합니다.
/// </summary>
/// <remarks><b>[Thread Safety:]</b> Snapshot thread-safe. <b>[Blocking:]</b> Non-blocking.</remarks>
public sealed class SlowlorisScenario : IStressScenario
{
    private readonly int _count;
    private readonly string _host;
    private readonly int _port;
    private readonly bool _drip;

    private readonly List<StalledPeer> _peers = new();
    private Task? _driveTask;
    private CancellationTokenSource? _cts;

    /// <summary>시나리오를 생성합니다.</summary>
    public SlowlorisScenario(int count, string host, int port, bool drip)
    {
        _count = count;
        _host = host;
        _port = port;
        _drip = drip;
    }

    /// <inheritdoc/>
    public StressScenarioKind Kind => StressScenarioKind.Slowloris;

    /// <inheritdoc/>
    public StressExecutionModel Model => StressExecutionModel.InProcess;

    /// <inheritdoc/>
    public StressExpectations Expectations => new(
        ExpectSessionCountRecovery: false, ExpectServerWsRecovery: false,
        HeadlineFinding: "idle sweep 미배선 → 정체 세션이 영원히 누적(회복=영영 안 됨), 피어당 워킹셋 증가");

    /// <inheritdoc/>
    public Task DriveAsync(StressRunContext context, CancellationToken stressToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stressToken);
        for (int i = 0; i < _count; i++)
            _peers.Add(new StalledPeer(_host, _port, _drip));
        _driveTask = Task.WhenAll(_peers.Select(p => p.RunAsync(_cts.Token)));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task ReleaseAsync()
    {
        _cts?.Cancel();
        if (_driveTask is not null)
        {
            try { await _driveTask.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) { /* 무시 */ }
        }
        _cts?.Dispose();
    }

    /// <inheritdoc/>
    public StressDriverSnapshot Snapshot()
    {
        int held = 0;
        foreach (StalledPeer p in _peers)
            if (p.IsHeld) held++;
        return new StressDriverSnapshot(
            StressConnectAttempts: _peers.Count, StressConnectFailures: 0,
            StressActive: held, StressReconnects: 0,
            MalformedFramesSent: 0, StalledHeld: held);
    }
}
