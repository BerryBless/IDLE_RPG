namespace LoadTester.Coordination;

/// <summary>
/// 코디네이터가 총 클라이언트를 K개 워커에 분할하고, 각 클라이언트를 P개 서버 포트에 분산하는
/// 순수 계산 유틸리티입니다. 전부 무상태 정적 함수라 단위 테스트로 완전히 검증됩니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe(무상태). <b>[Memory Allocation:]</b> Zero-allocation(값 계산).
/// <b>[Blocking:]</b> Non-blocking.
/// </remarks>
public static class WorkerShard
{
    /// <summary>총 클라이언트를 K개 워커에 연속·비중첩으로 분할한 뒤, 지정 워커의 (개수, 전역 시작 오프셋)을 반환합니다.</summary>
    /// <param name="totalClients">전체 클라이언트 수(1 이상).</param>
    /// <param name="workers">워커 수(1 이상).</param>
    /// <param name="workerIndex">워커 인덱스(0 이상 workers 미만).</param>
    /// <returns>이 워커가 담당할 클라이언트 개수와 전역 인덱스 시작 오프셋. 나머지는 앞쪽 워커에 1씩 배분된다.</returns>
    /// <remarks>모든 워커의 Count 합은 정확히 totalClients이며, [Offset, Offset+Count) 구간은 서로 겹치지 않고 연속이다.</remarks>
    public static (int Count, int Offset) ForWorker(int totalClients, int workers, int workerIndex)
    {
        if (totalClients < 1)
            throw new ArgumentOutOfRangeException(nameof(totalClients), totalClients, "총 클라이언트는 1 이상이어야 합니다.");
        if (workers < 1)
            throw new ArgumentOutOfRangeException(nameof(workers), workers, "워커 수는 1 이상이어야 합니다.");
        if (workerIndex < 0 || workerIndex >= workers)
            throw new ArgumentOutOfRangeException(nameof(workerIndex), workerIndex, "워커 인덱스는 [0, workers) 범위여야 합니다.");

        int baseCount = totalClients / workers;
        int remainder = totalClients % workers;
        // 나머지 r개를 앞쪽 워커 0..r-1에 1개씩 배분 → 오프셋도 그만큼 앞으로 당겨진다.
        int count = baseCount + (workerIndex < remainder ? 1 : 0);
        int offset = workerIndex * baseCount + Math.Min(workerIndex, remainder);
        return (count, offset);
    }

    /// <summary>전역 클라이언트 인덱스를 P개 서버 포트에 라운드로빈 매핑한 목적지 포트를 반환합니다.</summary>
    /// <param name="basePort">서버 게임 포트 시작값.</param>
    /// <param name="portCount">서버가 연 게임 포트 수(1 이상).</param>
    /// <param name="globalClientIndex">전역 클라이언트 인덱스(0 이상).</param>
    /// <returns><c>basePort + globalClientIndex % portCount</c>. 워커 오프셋이 연속이라 포트 분포도 균등하다.</returns>
    public static int SelectPort(int basePort, int portCount, int globalClientIndex)
    {
        if (portCount < 1)
            throw new ArgumentOutOfRangeException(nameof(portCount), portCount, "포트 수는 1 이상이어야 합니다.");
        return basePort + globalClientIndex % portCount;
    }
}
