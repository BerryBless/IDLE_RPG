namespace LoadTester.Client;

/// <summary>
/// 재접속 지연 정책: 지수 백오프(기본 3초, ×2, 상한 60초)에 ±20% 지터를 더해
/// 대량 클라이언트가 동시에 재접속을 재시도하는 thundering herd를 분산시킵니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not thread-safe — <see cref="Random"/> 인스턴스가
/// 상태를 가지므로 클라이언트(VirtualClient)마다 전용 인스턴스를 소유해야 한다.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation(값 계산만).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 지연 자체는 호출자가 Task.Delay로 수행.</description></item>
/// </list>
/// </remarks>
public sealed class ReconnectPolicy
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _baseDelay;
    private readonly Random _random;

    /// <summary>정책을 생성합니다.</summary>
    /// <param name="baseDelay">1회차 재시도 지연(지수 증가의 밑).</param>
    /// <param name="random">지터 생성기. 테스트에서 시드 고정 인스턴스를 주입해 결정적으로 검증한다.</param>
    public ReconnectPolicy(TimeSpan baseDelay, Random random)
    {
        _baseDelay = baseDelay;
        _random = random;
    }

    /// <summary>attempt회차(1부터) 재시도 전 대기 시간을 계산합니다.</summary>
    /// <param name="attempt">연속 실패 횟수(1 이상). 성공 시 호출자가 0으로 리셋한다.</param>
    /// <returns>지수 백오프 + ±20% 지터가 적용된 지연. 상한 60초(지터 적용 전 기준).</returns>
    public TimeSpan NextDelay(int attempt)
    {
        if (attempt < 1)
            attempt = 1;

        // 지수 계산 중 오버플로 방지: 2^(attempt-1)이 cap을 넘으면 그 이상 계산하지 않는다.
        double multiplier = Math.Pow(2, Math.Min(attempt - 1, 20));
        double baseMs = Math.Min(_baseDelay.TotalMilliseconds * multiplier, MaxDelay.TotalMilliseconds);

        // ±20% 지터: [0.8, 1.2) 균등 분포 — 동일 시각에 끊긴 수천 클라이언트의 재시도 시점을 흩뿌린다.
        double jitterFactor = 0.8 + _random.NextDouble() * 0.4;
        return TimeSpan.FromMilliseconds(baseMs * jitterFactor);
    }
}
