// 픽스처: concurrency-guard 하네스가 탐지해야 할 8개 동시성 안티패턴을 카테고리당 1개씩 심어놓은 소스.
// tests/fixtures/ 아래(모든 .csproj 폴더 밖)에 있어 컴파일되지 않는다 —
// concurrency-guard-orchestrator가 `find -name "*.cs"`로 읽어들일 때만 소비된다.

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Fixtures.ConcurrencyGuard;

public sealed class LockAndAsync
{
    private readonly object _sync = new();
    private readonly object _multiFieldSync = new();
    private long _counter;
    private long _a;
    private long _b;
    private readonly HttpClient _http = new();
    private readonly SemaphoreSlim _sem = new(1, 1);
    private readonly SemaphoreSlim _semA = new(1, 1);
    private readonly SemaphoreSlim _semB = new(1, 1);

    // CC1: replaceable — 단일 카운터 증가는 Interlocked로 대체 가능한데 lock을 씀
    public void IncrementCounter()
    {
        lock (_sync)
        {
            _counter++; // CC1: replaceable
        }
    }

    // CC2: missing — 두 필드를 원자적으로 갱신해야 해서 락이 실제로 필요하지만 [LOCK-REQUIRED] 주석이 없음
    public void UpdateBothFields(long a, long b)
    {
        lock (_multiFieldSync) // CC2: missing justification comment
        {
            _a = a;
            _b = b;
        }
    }

    // CC3: sync-blocking — async 메서드에서 .Result로 동기 블로킹 (CRITICAL)
    public string FetchSync(string url)
    {
        return _http.GetStringAsync(url).Result; // CC3: sync-blocking
    }

    // CC4: lock-await — Monitor.Enter 후 락을 잡은 채로 await (CRITICAL)
    public async Task LockThenAwaitAsync()
    {
        Monitor.Enter(_sync);
        try
        {
            await Task.Delay(10); // CC4: lock-await
        }
        finally
        {
            Monitor.Exit(_sync);
        }
    }

    // CC5: semaphore-leak — WaitAsync 후 try/finally 없이 Release 호출 (예외 시 영구 블로킹)
    public async Task UseSemaphoreAsync()
    {
        await _sem.WaitAsync();
        var result = await ProcessAsync(); // CC5: semaphore-leak — 예외 발생 시 Release 누락
        _sem.Release();
    }

    // CC6: configure-await — 라이브러리 코드에서 ConfigureAwait(false) 누락
    public async Task<string> LibraryCallAsync(string url)
    {
        return await _http.GetStringAsync(url); // CC6: configure-await 누락
    }

    // CC7: async-void — 이벤트 핸들러가 아닌데 async void 사용 (예외 처리 불가)
    public async void DoBackgroundWork()
    {
        await Task.Delay(1); // CC7: async-void
    }

    // CC8: lock-order — 두 세마포어를 서로 다른 순서로 취득해 데드락 가능
    public async Task AcquireAThenBAsync()
    {
        await _semA.WaitAsync();
        await _semB.WaitAsync(); // CC8: lock-order (A→B)
        _semB.Release();
        _semA.Release();
    }

    public async Task AcquireBThenAAsync()
    {
        await _semB.WaitAsync();
        await _semA.WaitAsync(); // CC8: lock-order (B→A) — AcquireAThenBAsync와 반대 순서
        _semA.Release();
        _semB.Release();
    }

    private Task<int> ProcessAsync() => Task.FromResult(0);
}
