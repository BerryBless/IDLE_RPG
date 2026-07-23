using LoadTester.Stress.Clients;

namespace LoadTester.Stress.Scenarios;

/// <summary>
/// 비정상/악의적 패킷 시나리오(인프로세스). 적대적 클라이언트 풀이 회전 악성 프레임을 인증 게이트에
/// 쏟아붓고, 서버가 크래시 없이 앱계층에서 거부(ack-false/무시)하는지, 그리고 종료하지 않는 세션이
/// 누적되는지를 측정합니다.
/// </summary>
/// <remarks><b>[Thread Safety:]</b> Snapshot은 thread-safe(클라이언트 카운터 합산). <b>[Blocking:]</b> Non-blocking.</remarks>
public sealed class MalformedScenario : IStressScenario
{
    private readonly int _count;
    private readonly string _host;
    private readonly int _port;

    private readonly List<AdversarialClient> _clients = new();
    private Task? _driveTask;
    private CancellationTokenSource? _cts;

    /// <summary>시나리오를 생성합니다.</summary>
    public MalformedScenario(int count, string host, int port)
    {
        _count = count;
        _host = host;
        _port = port;
    }

    /// <inheritdoc/>
    public StressScenarioKind Kind => StressScenarioKind.Malformed;

    /// <inheritdoc/>
    public StressExecutionModel Model => StressExecutionModel.InProcess;

    /// <inheritdoc/>
    public StressExpectations Expectations => new(
        ExpectSessionCountRecovery: false, ExpectServerWsRecovery: false,
        HeadlineFinding: "악성 프레임은 앱계층 ack-false/무시로 거부되지만 세션이 종료되지 않아 누적된다");

    /// <inheritdoc/>
    public Task DriveAsync(StressRunContext context, CancellationToken stressToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stressToken);
        for (int i = 0; i < _count; i++)
            _clients.Add(new AdversarialClient(i, _host, _port));
        _driveTask = Task.WhenAll(_clients.Select(c => c.RunAsync(_cts.Token)));
        return Task.CompletedTask; // 즉시 반환 — 러너는 During 동안 샘플링, Release에서 정리
    }

    /// <inheritdoc/>
    public async Task ReleaseAsync()
    {
        _cts?.Cancel();
        if (_driveTask is not null)
        {
            try { await _driveTask.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception) { /* 드레인 타임아웃/취소 무시 */ }
        }
        _cts?.Dispose();
    }

    /// <inheritdoc/>
    public StressDriverSnapshot Snapshot()
    {
        long frames = 0;
        int active = 0;
        foreach (AdversarialClient c in _clients)
        {
            frames += c.FramesSent;
            if (c.IsConnected) active++;
        }
        return new StressDriverSnapshot(
            StressConnectAttempts: _clients.Count, StressConnectFailures: 0,
            StressActive: active, StressReconnects: 0,
            MalformedFramesSent: frames, StalledHeld: active);
    }
}
