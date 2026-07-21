namespace LoadTester.Metrics;

/// <summary>
/// 다수 I/O 스레드가 초당 수만~수십만 회 증가시키는 핫 카운터(브로드캐스트 수·수신 바이트)용
/// 스트라이프 카운터입니다. 코어 수만큼의 슬롯에 캐시라인 패딩을 두고 스레드가 자기 프로세서의
/// 슬롯에만 기록해, 단일 필드 Interlocked의 캐시라인 핑퐁(경합)을 제거합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="Add"/>는 어느 스레드에서든
/// 동시 호출 가능(슬롯 내부는 Interlocked, 슬롯 간은 물리적으로 분리).</description></item>
/// <item><description><b>Memory Allocation:</b> 생성 시 long[코어수×8] 1회. 이후 무할당.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. lock-free 원자 연산만 사용.</description></item>
/// <item><description><b>정확도:</b> <see cref="Sum"/>은 슬롯을 순차 합산하므로 진행 중인 증가와
/// 겹치면 순간적으로 근사치일 수 있다 — 통계 표시 목적에는 무해.</description></item>
/// </list>
/// </remarks>
public sealed class StripedLongCounter
{
    // 슬롯당 long 8개(64바이트) 간격: x64 캐시라인 크기만큼 벌려 서로 다른 코어의 슬롯이
    // 같은 캐시라인을 공유(false sharing)해 무효화 트래픽을 주고받는 것을 막는다.
    private const int PaddingLongs = 8;

    private readonly long[] _slots;
    private readonly int _stripeCount;

    /// <summary>논리 프로세서 수만큼의 스트라이프로 카운터를 생성합니다.</summary>
    public StripedLongCounter()
    {
        _stripeCount = Environment.ProcessorCount;
        _slots = new long[_stripeCount * PaddingLongs];
    }

    /// <summary>현재 스레드가 실행 중인 프로세서의 스트라이프에 값을 누적합니다.</summary>
    /// <param name="value">더할 값.</param>
    public void Add(long value)
    {
        // Thread.GetCurrentProcessorId: 스레드가 지금 실행 중인 코어 번호 — 같은 코어에서 도는
        // 스레드들은 같은 슬롯을 쓰므로 슬롯 수가 코어 수로 상한되고, 코어 간 경합은 발생하지 않는다.
        // (마이그레이션 직후 잠깐 다른 코어 슬롯에 쓸 수 있으나 Interlocked라 정확성은 유지된다.)
        int stripe = Thread.GetCurrentProcessorId() % _stripeCount;
        Interlocked.Add(ref _slots[stripe * PaddingLongs], value);
    }

    /// <summary>1을 누적합니다.</summary>
    public void Increment() => Add(1);

    /// <summary>모든 스트라이프의 합을 반환합니다.</summary>
    public long Sum()
    {
        long sum = 0;
        for (int i = 0; i < _stripeCount; i++)
            sum += Interlocked.Read(ref _slots[i * PaddingLongs]);
        return sum;
    }
}
