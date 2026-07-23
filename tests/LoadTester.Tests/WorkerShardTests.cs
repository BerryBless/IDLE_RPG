using LoadTester.Coordination;

namespace LoadTester.Tests;

/// <summary><see cref="WorkerShard"/>의 분할·포트 매핑 순수 함수 검증.</summary>
public class WorkerShardTests
{
    [Theory]
    [InlineData(300000, 8)]
    [InlineData(10, 3)]
    [InlineData(1, 8)]     // 워커 > 클라이언트: 앞 워커만 1개씩
    [InlineData(100, 1)]
    [InlineData(7, 7)]
    public void ForWorker_합은_전체이고_구간은_연속비중첩(int total, int workers)
    {
        int sum = 0;
        int expectedOffset = 0;
        for (int i = 0; i < workers; i++)
        {
            (int count, int offset) = WorkerShard.ForWorker(total, workers, i);
            Assert.True(count >= 0);
            Assert.Equal(expectedOffset, offset); // 연속·비중첩
            sum += count;
            expectedOffset += count;
        }
        Assert.Equal(total, sum); // 합 = 전체
    }

    [Fact]
    public void ForWorker_나머지는_앞쪽워커에_1씩_배분()
    {
        // 10/3 = 3,3,3 + 나머지 1 → 4,3,3
        Assert.Equal((4, 0), WorkerShard.ForWorker(10, 3, 0));
        Assert.Equal((3, 4), WorkerShard.ForWorker(10, 3, 1));
        Assert.Equal((3, 7), WorkerShard.ForWorker(10, 3, 2));
    }

    [Fact]
    public void ForWorker_잘못된_인자_예외()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerShard.ForWorker(0, 3, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerShard.ForWorker(10, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerShard.ForWorker(10, 3, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerShard.ForWorker(10, 3, -1));
    }

    [Theory]
    [InlineData(7777, 8, 0, 7777)]
    [InlineData(7777, 8, 7, 7784)]
    [InlineData(7777, 8, 8, 7777)]   // 래핑
    [InlineData(7777, 8, 15, 7784)]
    [InlineData(7777, 1, 12345, 7777)] // P=1 항등
    public void SelectPort_라운드로빈(int basePort, int portCount, int globalIndex, int expected)
    {
        Assert.Equal(expected, WorkerShard.SelectPort(basePort, portCount, globalIndex));
    }

    [Fact]
    public void SelectPort_연속인덱스는_포트에_균등분포()
    {
        const int P = 4;
        var counts = new int[P];
        for (int i = 0; i < 4000; i++)
            counts[WorkerShard.SelectPort(7777, P, i) - 7777]++;
        Assert.All(counts, c => Assert.Equal(1000, c));
    }

    [Fact]
    public void SelectPort_포트수_0이하_예외()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerShard.SelectPort(7777, 0, 0));
    }

    // 하네스 전체가 의존하는 핵심 불변식: (sourcePort, dstPort) = (srcBase + i/P, gameBase + i%P)가
    // 전역 인덱스에 대해 충돌 없는 전단사여야 한다(같은 소스 포트를 P개 목적지에 재사용하되 4-튜플은 유일).
    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(1)]
    public void 소스_목적지_4튜플은_전역_유일하다(int portCount)
    {
        const int gameBase = 20000, srcBase = 25000, count = 12_000;
        var seen = new HashSet<(int Src, int Dst)>();
        for (int i = 0; i < count; i++)
        {
            int dst = WorkerShard.SelectPort(gameBase, portCount, i);
            int src = srcBase + i / portCount; // VirtualClient의 소스 포트 산식과 동일
            Assert.True(seen.Add((src, dst)), $"4-튜플 충돌: index {i} → (src {src}, dst {dst})");
        }
        Assert.Equal(count, seen.Count);
    }
}
