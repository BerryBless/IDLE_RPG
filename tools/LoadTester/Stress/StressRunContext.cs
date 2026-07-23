using LoadTester.Auth;
using LoadTester.Metrics;
using LoadTester.Options;
using LoadTester.Telemetry;

namespace LoadTester.Stress;

/// <summary>
/// 스트레스 실행 전반에서 시나리오 드라이버가 공유하는 컨텍스트입니다. StressRunner가 조립해 주입합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> 불변 참조 묶음 — 담긴 객체 각각의 thread-safety를 따른다. <b>[Blocking:]</b> N/A.
/// </remarks>
public sealed class StressRunContext
{
    /// <summary>실행 옵션.</summary>
    public required LoadTestOptions Options { get; init; }

    /// <summary>game 모드 토큰 소스(버스트/churn 프로브·워커와 동일 시크릿).</summary>
    public required ITokenSource TokenSource { get; init; }

    /// <summary>서버 텔레메트리 구독(누적·회복 신호). 없으면 null.</summary>
    public TelemetrySubscriber? Telemetry { get; init; }

    /// <summary>서버·자기 리소스 모니터.</summary>
    public required ResourceMonitor Resources { get; init; }

    /// <summary>NDJSON 등 출력 루트.</summary>
    public required string OutDirectory { get; init; }
}
