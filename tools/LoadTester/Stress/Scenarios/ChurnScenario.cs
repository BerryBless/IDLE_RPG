using LoadTester.Options;

namespace LoadTester.Stress.Scenarios;

/// <summary>
/// 연결 churn 시나리오(멀티프로세스). 워커 함대가 접속→인증→즉시 종료→재접속을 백오프 없이 고속
/// 반복해, 서버의 접속/인증/세션정리 처리율과 세션 수 안정성(누수 없이 낮게 유지되는지)을 측정합니다.
/// 병목은 대개 클라이언트측 TIME_WAIT/임시 포트입니다. 정상 프로브가 그 와중에도 유지되는지 병행 관측합니다.
/// </summary>
/// <remarks><b>[Thread Safety:]</b> Snapshot thread-safe(fleet). <b>[Blocking:]</b> Non-blocking.</remarks>
public sealed class ChurnScenario : IStressScenario
{
    private readonly LoadTestOptions _options;
    private readonly string _outRoot;
    private WorkerFleet? _fleet;

    /// <summary>시나리오를 생성합니다.</summary>
    public ChurnScenario(LoadTestOptions options, string outRoot)
    {
        _options = options;
        _outRoot = outRoot;
    }

    /// <inheritdoc/>
    public StressScenarioKind Kind => StressScenarioKind.Churn;

    /// <inheritdoc/>
    public StressExecutionModel Model => StressExecutionModel.MultiProcess;

    /// <inheritdoc/>
    public StressExpectations Expectations => new(
        ExpectSessionCountRecovery: true, ExpectServerWsRecovery: true,
        HeadlineFinding: "고속 재접속 — 서버 세션 수는 낮게 안정, 병목은 클라측 TIME_WAIT/임시 포트");

    /// <inheritdoc/>
    public Task DriveAsync(StressRunContext context, CancellationToken stressToken)
    {
        int target = _options.StressTarget ?? _options.Clients;
        var workerOptions = _options with
        {
            Clients = target,
            RampUpPerSecond = Math.Max(_options.RampUpPerSecond, 20_000), // 고속 churn 구동
            Churn = true,   // 워커 VirtualClient가 인증 후 즉시 종료·재접속
            Stress = null,
        };
        _fleet = new WorkerFleet(workerOptions, _options.Workers, _outRoot);
        _fleet.Start();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task ReleaseAsync()
    {
        if (_fleet is not null)
            await _fleet.StopAsync();
    }

    /// <inheritdoc/>
    public StressDriverSnapshot Snapshot() => _fleet?.Snapshot() ?? default;
}
