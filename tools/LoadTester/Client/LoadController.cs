using LoadTester.Auth;
using LoadTester.Metrics;
using LoadTester.Options;

namespace LoadTester.Client;

/// <summary>
/// 가상 클라이언트 N개의 생성·기동·종료 대기를 담당합니다. 클라이언트마다 전용
/// <see cref="ReconnectPolicy"/>(자체 Random)를 부여하고, 연결 속도는 공유
/// <see cref="ConnectPacer"/>로 일괄 제한합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <see cref="RunAsync"/>는 1회 호출 전용.
/// <see cref="Clients"/>는 생성 후 불변 리스트라 샘플러가 동시 순회해도 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 기동 시 클라이언트 N개 + 태스크 N개 할당(1회).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 전 클라이언트 태스크를 비동기로 대기.</description></item>
/// </list>
/// </remarks>
public sealed class LoadController
{
    private readonly List<VirtualClient> _clients;

    /// <summary>생성된 가상 클라이언트 목록(불변). 샘플러가 스냅샷 순회에 사용한다.</summary>
    public IReadOnlyList<VirtualClient> Clients => _clients;

    /// <summary>컨트롤러를 생성하고 클라이언트 N개를 준비합니다(아직 연결하지 않음).</summary>
    /// <param name="options">실행 옵션.</param>
    /// <param name="tokens">토큰 획득 전략(모드에 따라 주입).</param>
    /// <param name="metrics">공유 카운터.</param>
    public LoadController(LoadTestOptions options, ITokenSource tokens, MetricsAggregator metrics)
    {
        var pacer = new ConnectPacer(options.RampUpPerSecond);
        _clients = new List<VirtualClient>(options.Clients);
        for (int i = 0; i < options.Clients; i++)
        {
            // Random.Shared가 아닌 개별 인스턴스: ReconnectPolicy는 Not thread-safe 계약이므로
            // 클라이언트 태스크마다 전용 Random을 소유시킨다(시드는 인덱스 파생으로 재현성 확보).
            // 전역 인덱스 = 워커 오프셋 + 로컬 인덱스. 계정 매핑(clientIndex % accounts)과 포트 분산이
            // 워커 간 전역적으로 유일·균등해지도록 오프셋을 더한다. 단일 프로세스면 오프셋 0이라 기존과 동일.
            int globalIndex = options.ClientIndexOffset + i;
            var policy = new ReconnectPolicy(options.ReconnectDelay, new Random(HashCode.Combine(globalIndex)));
            _clients.Add(new VirtualClient(globalIndex, options, tokens, metrics, policy, pacer));
        }
    }

    /// <summary>전 클라이언트를 기동하고 수명 토큰 취소 시까지 대기합니다.</summary>
    /// <param name="lifetime">전체 실행 수명 토큰.</param>
    public async Task RunAsync(CancellationToken lifetime)
    {
        var tasks = new Task[_clients.Count];
        for (int i = 0; i < _clients.Count; i++)
            tasks[i] = _clients[i].RunAsync(lifetime);
        await Task.WhenAll(tasks);
    }
}
