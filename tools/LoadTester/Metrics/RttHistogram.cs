namespace LoadTester.Metrics;

/// <summary>
/// RTT 백분위 계산용 고정 크기 하이브리드 해상도 히스토그램입니다.
/// 0–20ms@0.1ms(200) · 20–200ms@1ms(180) · 200ms–2s@10ms(180) · 2–60s@250ms(232) · 오버플로(1)
/// = 총 793버킷 ≈ 6.3KB로, 72시간 실행에도 메모리 사용량이 상수로 고정됩니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not thread-safe — <b>단일 라이터 전제</b>.
/// 샘플러 스레드만 <see cref="Record"/>/<see cref="PercentileMs"/>/<see cref="CopyAndReset"/>를
/// 호출하는 설계라 Interlocked조차 불필요하다(설계상 유일 접근자).</description></item>
/// <item><description><b>Memory Allocation:</b> 생성 시 long[793] 1회. <see cref="Record"/>는 무할당,
/// <see cref="CopyAndReset"/>만 새 인스턴스 1개를 할당한다.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 배열 인덱스 연산만 수행.</description></item>
/// </list>
/// </remarks>
public sealed class RttHistogram
{
    // 구간 경계(ms)와 해상도. 저지연 구간일수록 촘촘하게 — RTT 판정 임계치(수백 ms)가
    // 걸리는 구간에서 오차가 해상도(≤10ms) 이내가 되도록 설계.
    private const double Band1UpperMs = 20;      // @0.1ms → 200버킷
    private const double Band2UpperMs = 200;     // @1ms   → 180버킷
    private const double Band3UpperMs = 2_000;   // @10ms  → 180버킷
    private const double Band4UpperMs = 60_000;  // @250ms → 232버킷
    private const int Band1Start = 0;
    private const int Band2Start = 200;
    private const int Band3Start = 380;
    private const int Band4Start = 560;
    private const int OverflowIndex = 792;
    internal const int BucketCount = 793;

    // long[]: 고정 크기 배열 1개가 히스토그램 전체 — 버킷당 8바이트, 총 6.3KB.
    // 단일 라이터 전제라 원자 연산 없이 일반 증가로 기록한다.
    private readonly long[] _buckets = new long[BucketCount];
    private long _total;

    /// <summary>기록된 총 샘플 수.</summary>
    public long TotalCount => _total;

    /// <summary>RTT 샘플 1건을 기록합니다. 60초 초과는 오버플로 버킷에 누적됩니다.</summary>
    /// <param name="rtt">기록할 왕복 지연.</param>
    public void Record(TimeSpan rtt)
    {
        _buckets[BucketIndex(rtt.TotalMilliseconds)]++;
        _total++;
    }

    /// <summary>주어진 밀리초 값이 속하는 버킷 인덱스를 계산합니다(순수 함수, 테스트 대상).</summary>
    internal static int BucketIndex(double ms)
    {
        if (ms < 0)
            return 0;
        if (ms < Band1UpperMs)
            return Band1Start + (int)(ms / 0.1);
        if (ms < Band2UpperMs)
            return Band2Start + (int)((ms - Band1UpperMs) / 1);
        if (ms < Band3UpperMs)
            return Band3Start + (int)((ms - Band2UpperMs) / 10);
        if (ms < Band4UpperMs)
            return Band4Start + (int)((ms - Band3UpperMs) / 250);
        return OverflowIndex;
    }

    /// <summary>버킷 인덱스의 대표값(구간 중앙, ms)을 반환합니다. 오버플로 버킷은 60,000ms.</summary>
    internal static double BucketMidpointMs(int index)
    {
        if (index < Band2Start)
            return (index + 0.5) * 0.1;
        if (index < Band3Start)
            return Band1UpperMs + (index - Band2Start + 0.5) * 1;
        if (index < Band4Start)
            return Band2UpperMs + (index - Band3Start + 0.5) * 10;
        if (index < OverflowIndex)
            return Band3UpperMs + (index - Band4Start + 0.5) * 250;
        return Band4UpperMs;
    }

    /// <summary>p 백분위(0 초과 100 이하) RTT를 밀리초로 반환합니다. 샘플이 없으면 0.</summary>
    /// <param name="p">백분위(예: 50, 95, 99).</param>
    public double PercentileMs(double p)
    {
        if (_total == 0)
            return 0;
        if (p <= 0 || p > 100)
            throw new ArgumentOutOfRangeException(nameof(p), p, "백분위는 (0, 100] 범위여야 합니다.");

        long target = (long)Math.Ceiling(p / 100.0 * _total);
        long cumulative = 0;
        for (int i = 0; i < BucketCount; i++)
        {
            cumulative += _buckets[i];
            if (cumulative >= target)
                return BucketMidpointMs(i);
        }
        return BucketMidpointMs(OverflowIndex);
    }

    /// <summary>현재 내용의 사본을 반환하고 자신을 0으로 초기화합니다(구간 히스토그램 스냅샷용).</summary>
    public RttHistogram CopyAndReset()
    {
        var copy = new RttHistogram();
        Array.Copy(_buckets, copy._buckets, BucketCount);
        copy._total = _total;
        Array.Clear(_buckets);
        _total = 0;
        return copy;
    }
}
