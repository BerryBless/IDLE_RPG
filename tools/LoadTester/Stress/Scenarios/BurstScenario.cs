using LoadTester.Options;

namespace LoadTester.Stress.Scenarios;

/// <summary>
/// 스파이크/버스트 과부하 시나리오(멀티프로세스). During 시작 시 워커 함대가 램프 없이(초고 ramp-up)
/// 과부하 목표(측정 상한 초과)만큼 한꺼번에 접속을 몰아붙이고, Release에서 워커를 강제 종료(대량 FIN/RST)해
/// 서버가 드레인·회복하는지를 측정합니다. 정상 프로브는 전 구간 병행되어 서비스 유지·회복을 증명합니다.
/// </summary>
/// <remarks><b>[Thread Safety:]</b> Snapshot thread-safe(fleet). <b>[Blocking:]</b> Non-blocking.</remarks>
public sealed class BurstScenario : IStressScenario
{
    private readonly LoadTestOptions _options;
    private readonly string _outRoot;
    private WorkerFleet? _fleet;

    /// <summary>시나리오를 생성합니다.</summary>
    public BurstScenario(LoadTestOptions options, string outRoot)
    {
        _options = options;
        _outRoot = outRoot;
    }

    /// <inheritdoc/>
    public StressScenarioKind Kind => StressScenarioKind.Burst;

    /// <inheritdoc/>
    public StressExecutionModel Model => StressExecutionModel.MultiProcess;

    /// <inheritdoc/>
    public StressExpectations Expectations => new(
        ExpectSessionCountRecovery: true, ExpectServerWsRecovery: true,
        HeadlineFinding: "과부하 급습 → 초과 연결 거부/타임아웃, 해제 후 세션·워킹셋이 기준선으로 복귀(누수 없음)");

    /// <inheritdoc/>
    public Task DriveAsync(StressRunContext context, CancellationToken stressToken)
    {
        int target = _options.StressTarget ?? _options.Clients;
        // 워커 옵션: 램프 사실상 제거(thundering herd), 과부하 목표, 소스 포트 재사용(멀티포트).
        var workerOptions = _options with
        {
            Clients = target,
            RampUpPerSecond = 1_000_000, // ConnectPacer가 램프를 사실상 부과하지 않음
            Stress = null,               // 워커는 스트레스 러너가 아니라 --role worker 경로를 탄다
            Churn = false,
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
