namespace LoadTester.Client;

/// <summary>
/// 연결 시도 속도 제한기: 초당 연결 수(램프업)와 동시 진행 TCP 핸드셰이크 수를 함께 제한합니다.
/// 초기 램프업과 재접속이 같은 페이서를 공유해, 서버 재시작 직후 수천 클라이언트가 일제히
/// 재접속하는 thundering herd에서도 서버 accept 큐와 로컬 포트 소모 속도를 일정하게 유지합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 다수 VirtualClient 태스크가 동시에
/// <see cref="WaitAsync"/>/<see cref="Release"/>를 호출한다.</description></item>
/// <item><description><b>Memory Allocation:</b> 대기 시 세마포어 큐 노드 할당(저빈도).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking(비동기 대기). 스레드를 점유하지 않는다.</description></item>
/// </list>
/// </remarks>
public sealed class ConnectPacer
{
    /// <summary>동시 진행 가능한 TCP 핸드셰이크 상한.</summary>
    private const int MaxConcurrentConnects = 64;

    // SemaphoreSlim(비동기 WaitAsync): 수천 태스크가 연결 차례를 기다려도 스레드를 소비하지 않고
    // 관리형 큐에 줄만 선다 — Monitor 기반 lock으로는 불가능한 비동기 대기 상한 제어.
    private readonly SemaphoreSlim _concurrentGate = new(MaxConcurrentConnects, MaxConcurrentConnects);

    private readonly double _minIntervalTicks;

    // long + Interlocked.CompareExchange: 다음 연결이 허용되는 시각(Stopwatch tick)을 단일 원자
    // 변수로 관리한다. CAS 루프로 "슬롯 예약"을 원자화해 락 없이 초당 N개 속도를 강제한다.
    private long _nextSlotTicks;

    /// <summary>초당 연결 수 상한으로 페이서를 생성합니다.</summary>
    /// <param name="connectsPerSecond">초당 허용 연결 시도 수(1 이상).</param>
    public ConnectPacer(int connectsPerSecond)
    {
        if (connectsPerSecond < 1)
            throw new ArgumentOutOfRangeException(nameof(connectsPerSecond), connectsPerSecond, "초당 연결 수는 1 이상이어야 합니다.");
        _minIntervalTicks = (double)System.Diagnostics.Stopwatch.Frequency / connectsPerSecond;
        _nextSlotTicks = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    /// <summary>연결 시도 허가를 기다립니다. 반환 후 반드시 연결 완료/실패 시 <see cref="Release"/>를 호출해야 합니다.</summary>
    /// <param name="cancellationToken">대기 취소 토큰.</param>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        // 1) 속도 슬롯 예약: CAS로 자기 슬롯 시각을 원자적으로 확보한 뒤 그 시각까지 대기.
        long mySlot;
        while (true)
        {
            long current = Interlocked.Read(ref _nextSlotTicks);
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            // 슬롯이 과거에 밀려 있으면 현재 시각부터 재시작(유휴 후 폭주 방지: 미사용 슬롯은 이월하지 않는다)
            long baseTicks = Math.Max(current, now);
            mySlot = baseTicks + (long)_minIntervalTicks;
            if (Interlocked.CompareExchange(ref _nextSlotTicks, mySlot, current) == current)
            {
                long waitTicks = baseTicks - now;
                if (waitTicks > 0)
                {
                    var delay = TimeSpan.FromSeconds((double)waitTicks / System.Diagnostics.Stopwatch.Frequency);
                    await Task.Delay(delay, cancellationToken);
                }
                break;
            }
        }

        // 2) 동시 핸드셰이크 상한.
        await _concurrentGate.WaitAsync(cancellationToken);
    }

    /// <summary>연결 시도(성공/실패 무관)가 끝났음을 알려 동시 핸드셰이크 슬롯을 반납합니다.</summary>
    public void Release() => _concurrentGate.Release();
}
