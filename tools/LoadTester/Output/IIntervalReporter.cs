using LoadTester.Metrics;

namespace LoadTester.Output;

/// <summary>
/// 구간 리포트 1건을 어딘가로 내보내는 싱크 추상화입니다. 단일 프로세스·코디네이터는
/// <see cref="ConsoleReporter"/>(사람이 읽는 콘솔 1줄), 워커는
/// <see cref="LoadTester.Coordination.StatusLineEmitter"/>(코디네이터가 파싱하는 <c>@interval</c> 라인)로 구현합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> 구현체는 샘플러 스레드 단일 호출 전제. <b>[Blocking:]</b> 콘솔/stdout 동기 쓰기(수 µs).
/// </remarks>
public interface IIntervalReporter
{
    /// <summary>구간 리포트 1건을 내보냅니다.</summary>
    void Report(IntervalReport report);
}
