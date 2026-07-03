// 픽스처: gc-guard 하네스가 탐지해야 할 8개 GC 압력 안티패턴을 카테고리당 1개씩 심어놓은 소스.
// 주의: 이 파일은 tests/fixtures/ 아래(어떤 .csproj 폴더 밖)에 있어 SDK 글로빙에 잡히지 않는다 —
// 컴파일되지 않으며, 오직 gc-guard-orchestrator가 `find -name "*.cs"`로 읽어들일 때만 소비된다.

using System;
using System.Collections;
using System.Buffers;
using System.Linq;
using System.Threading.Tasks;

namespace Fixtures.GcGuard;

public sealed class HotPathAllocations
{
    private readonly System.Collections.Generic.List<Item> _cache = new();

    // GC1: new-in-loop — hot path(Handle*) 루프 안에서 매번 새 배열 힙 할당
    public async ValueTask HandlePacketsAsync(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var buf = new byte[1024]; // GC1: new-in-loop
            Consume(buf);
        }
        await Task.CompletedTask;
    }

    // GC2: boxing — ArrayList.Add(int)로 값 타입이 object로 박싱됨
    public void RecordScore(int score)
    {
        var list = new ArrayList();
        list.Add(score); // GC2: boxing
    }

    // GC3: linq-hotpath — 요청당 호출되는 hot path에서 Where/Select/ToList 연쇄
    public System.Collections.Generic.List<Dto> HandleQuery()
    {
        return _cache.Where(x => x.IsActive).Select(x => x.ToDto()).ToList(); // GC3: linq-hotpath
    }

    // GC4: closure-capture — 루프 변수 캡처로 람다마다 힙 할당 강제
    public Task[] ProcessAll(int n)
    {
        var tasks = new Task[n];
        for (var i = 0; i < n; i++)
        {
            tasks[i] = Task.Run(() => Process(i)); // GC4: closure-capture
        }
        return tasks;
    }

    // GC5: string-concat — hot path 루프에서 + 연산자로 문자열 반복 할당
    public string HandleFormat(System.Collections.Generic.List<Item> items)
    {
        string s = "";
        foreach (var it in items)
        {
            s += it.Name + ","; // GC5: string-concat
        }
        return s;
    }

    // GC6: arraypool-return-missing — Rent 후 try/finally 없이 Return 누락 (CRITICAL)
    public async ValueTask<int> HandleReceiveAsync(int size)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(size); // GC6: arraypool-return-missing
        var n = await ReadIntoAsync(buffer);
        return n; // Return 누락 — 예외/정상 경로 모두 반환 안 됨
    }

    // GC7: task-instead-of-valuetask — 동기 완료 경로가 많은 hot path 메서드가 Task<T> 반환
    public Task<byte[]> ReadFromCacheAsync(int key)
    {
        if (_hit.TryGetValue(key, out var cached))
            return Task.FromResult(cached); // GC7: task-instead-of-valuetask
        return Task.FromResult(Array.Empty<byte>());
    }

    // GC8: substring-copy — hot path에서 Substring으로 불필요한 문자열 복사
    public string HandleParseHeader(string input, int offset, int len)
    {
        return input.Substring(offset, len); // GC8: substring-copy
    }

    private readonly System.Collections.Generic.Dictionary<int, byte[]> _hit = new();
    private void Consume(byte[] buf) { }
    private void Process(int i) { }
    private ValueTask<int> ReadIntoAsync(byte[] buffer) => ValueTask.FromResult(0);
}

public sealed class Item
{
    public string Name { get; set; } = "";
    public bool IsActive { get; set; }
    public Dto ToDto() => new Dto { Name = Name };
}

public sealed class Dto
{
    public string Name { get; set; } = "";
}
