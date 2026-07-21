using LoadTester.Auth;
using LoadTester.Client;
using LoadTester.Metrics;
using LoadTester.Options;

namespace LoadTester.Stress;

/// <summary>정상 대조군 프로브의 건강도 스냅샷입니다.</summary>
/// <param name="Size">프로브 클라이언트 수.</param>
/// <param name="Connected">현재 연결 상태 수.</param>
/// <param name="Authenticated">현재 인증 상태 수.</param>
/// <param name="EverAuthenticated">한 번이라도 인증에 성공한 수.</param>
/// <param name="RttP50Ms">프로브 RTT p50(ms).</param>
/// <param name="RttP95Ms">프로브 RTT p95(ms).</param>
public readonly record struct ProbeHealthSnapshot(
    int Size, int Connected, int Authenticated, int EverAuthenticated, double RttP50Ms, double RttP95Ms);

/// <summary>
/// 스트레스 전/중/후 서버 건강도를 측정하는 소수의 정상 클라이언트 풀입니다. <see cref="VirtualClient"/>를
/// 그대로 재사용하되, 스트레스 클라이언트와 완전히 격리된 전용 메트릭·전용 히스토그램을 쓰고,
/// 멀티포트 워커의 소스 포트 밴드와 충돌하지 않도록 PortCount=1(OS 임시 포트)로 접속합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <see cref="RunAsync"/>는 1회 호출. <see cref="Sample"/>은
/// 샘플러 스레드 전용(전달된 scratch 히스토그램의 유일한 라이터).</description></item>
/// <item><description><b>Memory Allocation:</b> 생성 시 클라이언트 N개. <see cref="Sample"/>은 무할당
/// (scratch 히스토그램 재사용).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking.</description></item>
/// </list>
/// </remarks>
public sealed class ControlProbe
{
    private readonly List<VirtualClient> _clients;

    /// <summary>프로브 클라이언트 수.</summary>
    public int Size => _clients.Count;

    /// <summary>프로브를 생성합니다(아직 연결하지 않음).</summary>
    /// <param name="baseOptions">기준 옵션. 여기서 프로브 전용 옵션을 파생한다.</param>
    /// <param name="tokens">토큰 소스(스트레스 클라이언트와 동일 시크릿).</param>
    public ControlProbe(LoadTestOptions baseOptions, ITokenSource tokens)
    {
        // 프로브 전용 옵션: 단일 포트(OS 임시 소스 포트 → 워커 25000+ 밴드와 충돌 없음), 짧은 PING(RTT
        // 곡선 해상도), 전용 카운터. Clients=ProbeClients라 스냅샷 순회 대상이 프로브로 한정된다.
        var probeOptions = baseOptions with
        {
            Clients = baseOptions.ProbeClients,
            PortCount = 1,
            PingInterval = baseOptions.ProbePingInterval,
            RampUpPerSecond = Math.Max(50, baseOptions.ProbeClients), // 프로브는 빨리 채운다
            ClientIndexOffset = 0,
        };
        // 전용 MetricsAggregator: 스트레스 클라이언트 카운터와 섞이면 프로브 건강을 증명할 수 없다.
        var probeMetrics = new MetricsAggregator();
        var controller = new LoadController(probeOptions, tokens, probeMetrics);
        _clients = new List<VirtualClient>(controller.Clients);
    }

    /// <summary>전 페이즈 동안 프로브를 구동합니다(취소 시 정상 종료).</summary>
    public async Task RunAsync(CancellationToken lifetime)
    {
        var tasks = new Task[_clients.Count];
        for (int i = 0; i < _clients.Count; i++)
            tasks[i] = _clients[i].RunAsync(lifetime);
        await Task.WhenAll(tasks);
    }

    /// <summary>현재 프로브 건강도를 측정합니다(샘플러 전용). scratch 히스토그램에 RTT를 담아 백분위를 계산.</summary>
    /// <param name="rttScratch">RTT 백분위 계산용 히스토그램. 호출자가 소유하며 이 메서드가 리셋한다.</param>
    public ProbeHealthSnapshot Sample(RttHistogram rttScratch)
    {
        int connected = 0, authenticated = 0, everAuth = 0;
        foreach (VirtualClient client in _clients)
        {
            VirtualClientSnapshot snap = client.ReadSnapshot();
            if (snap.EverAuthenticated) everAuth++;
            if (!snap.Connected) continue;
            connected++;
            if (snap.Authenticated) authenticated++;
            if (snap.RttTicks > 0)
                rttScratch.Record(TimeSpan.FromTicks(snap.RttTicks));
        }
        double p50 = rttScratch.PercentileMs(50);
        double p95 = rttScratch.PercentileMs(95);
        rttScratch.CopyAndReset(); // 다음 샘플을 위해 비운다(사본은 버림)
        return new ProbeHealthSnapshot(_clients.Count, connected, authenticated, everAuth, p50, p95);
    }
}
