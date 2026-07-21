using System.Diagnostics;

namespace LoadTester.Metrics;

/// <summary>리소스 샘플 1회분입니다. 서버 필드는 모니터링 미설정/프로세스 소실 시 null.</summary>
/// <param name="ServerWorkingSetMb">서버 프로세스 워킹셋(MB).</param>
/// <param name="ServerCpuPercent">서버 프로세스 CPU 사용률(코어 수 정규화 %).</param>
/// <param name="ServerProcessLost">모니터링을 요청했으나 대상 프로세스를 찾지 못한 상태.</param>
/// <param name="SelfWorkingSetMb">LoadTester 자신 워킹셋(MB) — 툴 자체 누수 감시.</param>
/// <param name="SelfThreadCount">ThreadPool 스레드 수.</param>
/// <param name="SelfGen2Collections">누적 Gen2 GC 횟수.</param>
public readonly record struct ResourceSample(
    double? ServerWorkingSetMb, double? ServerCpuPercent, bool ServerProcessLost,
    double SelfWorkingSetMb, int SelfThreadCount, int SelfGen2Collections);

/// <summary>
/// 서버 프로세스(PID 또는 이름)와 LoadTester 자신의 리소스를 주기 샘플링합니다.
/// CPU%는 두 샘플 사이 <see cref="Process.TotalProcessorTime"/> 증가분을 벽시계 경과 ×
/// 코어 수로 나눠 계산합니다(외부 카운터 API 없이 BCL만 사용).
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Not thread-safe — 샘플러 스레드 전용
/// (이전 샘플 상태를 내부 보관).</description></item>
/// <item><description><b>Memory Allocation:</b> 프로세스 조회 시 Process 객체 할당
/// (10초 주기 — 무해). 대상 소실 시 재조회를 반복한다.</description></item>
/// <item><description><b>Blocking:</b> <see cref="Sample"/>은 OS 프로세스 정보 조회로
/// 수 ms 동기 블로킹 가능 — 샘플러 루프(10초 주기)에서만 호출할 것.</description></item>
/// </list>
/// </remarks>
public sealed class ResourceMonitor
{
    private readonly int? _pid;
    private readonly string? _processName;
    private readonly bool _monitoringRequested;

    private Process? _serverProcess;
    private TimeSpan _lastCpuTime;
    private DateTime _lastSampleUtc;

    /// <summary>모니터를 생성합니다. PID가 지정되면 이름보다 우선한다.</summary>
    /// <param name="pid">서버 프로세스 PID(선택).</param>
    /// <param name="processName">서버 프로세스 이름(선택, 확장자 제외).</param>
    public ResourceMonitor(int? pid, string? processName)
    {
        _pid = pid;
        _processName = processName;
        _monitoringRequested = pid is not null || processName is not null;
    }

    /// <summary>서버 리소스 모니터링이 요청되었는지 여부.</summary>
    public bool MonitoringRequested => _monitoringRequested;

    /// <summary>리소스를 1회 샘플링합니다.</summary>
    public ResourceSample Sample()
    {
        double selfWsMb = Environment.WorkingSet / (1024.0 * 1024.0);
        int selfThreads = ThreadPool.ThreadCount;
        int selfGen2 = GC.CollectionCount(2);

        if (!_monitoringRequested)
            return new ResourceSample(null, null, false, selfWsMb, selfThreads, selfGen2);

        try
        {
            EnsureServerProcess();
            if (_serverProcess is null)
                return new ResourceSample(null, null, true, selfWsMb, selfThreads, selfGen2);

            _serverProcess.Refresh();
            if (_serverProcess.HasExited)
            {
                _serverProcess = null;
                return new ResourceSample(null, null, true, selfWsMb, selfThreads, selfGen2);
            }

            double wsMb = _serverProcess.WorkingSet64 / (1024.0 * 1024.0);

            double? cpuPercent = null;
            TimeSpan cpuTime = _serverProcess.TotalProcessorTime;
            DateTime now = DateTime.UtcNow;
            if (_lastSampleUtc != default)
            {
                double wallMs = (now - _lastSampleUtc).TotalMilliseconds;
                if (wallMs > 0)
                {
                    double cpuMs = (cpuTime - _lastCpuTime).TotalMilliseconds;
                    cpuPercent = Math.Max(0, cpuMs / wallMs / Environment.ProcessorCount * 100.0);
                }
            }
            _lastCpuTime = cpuTime;
            _lastSampleUtc = now;

            return new ResourceSample(wsMb, cpuPercent, false, selfWsMb, selfThreads, selfGen2);
        }
        catch (Exception)
        {
            // 접근 거부·경합 종료 등 — 소실로 보고하고 다음 샘플에서 재조회.
            _serverProcess = null;
            return new ResourceSample(null, null, true, selfWsMb, selfThreads, selfGen2);
        }
    }

    private void EnsureServerProcess()
    {
        if (_serverProcess is not null)
            return;

        if (_pid is int pid)
        {
            try
            {
                _serverProcess = Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                _serverProcess = null; // 해당 PID 없음 — 소실 처리
            }
        }
        else if (_processName is not null)
        {
            Process[] candidates = Process.GetProcessesByName(_processName);
            _serverProcess = candidates.Length > 0 ? candidates[0] : null;
            // 첫 번째 이외 후보는 사용하지 않으므로 즉시 해제(핸들 누수 방지).
            for (int i = 1; i < candidates.Length; i++)
                candidates[i].Dispose();
        }

        // 대상이 바뀌었으면 CPU 델타 기준점 리셋(다른 프로세스의 CPU 시간과 섞이지 않게).
        _lastSampleUtc = default;
        _lastCpuTime = default;
    }
}
