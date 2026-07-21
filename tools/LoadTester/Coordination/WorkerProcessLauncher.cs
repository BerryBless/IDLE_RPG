using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using LoadTester.Options;

namespace LoadTester.Coordination;

/// <summary>
/// 코디네이터가 워커 프로세스를 스폰합니다. 현재 실행 파일을 <c>--role worker</c> 인자로 재기동하며,
/// stdout/stderr을 파이프로 받아 <c>@interval</c>/<c>@final</c> 라인과 사람용 로그를 분리 처리하게 합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> <see cref="Launch"/>는 정적 순수 스폰(호출 측이 순차 호출). <b>[Blocking:]</b>
/// Process.Start는 즉시 반환(비동기 실행). <b>[Memory:]</b> 프로세스당 Process 객체 1개.
/// </remarks>
public static class WorkerProcessLauncher
{
    /// <summary>워커 프로세스 1개를 스폰합니다(아직 <c>BeginOutputReadLine</c>은 호출자 몫).</summary>
    /// <param name="coordinatorOptions">코디네이터 옵션(공유 인자 원본).</param>
    /// <param name="workerIndex">워커 인덱스.</param>
    /// <param name="shardCount">이 워커가 담당할 클라이언트 수.</param>
    /// <param name="shardOffset">이 워커의 전역 인덱스 시작 오프셋.</param>
    /// <param name="outRoot">통합 출력 루트(워커는 <c>{outRoot}\worker-{i}</c>에 기록).</param>
    /// <returns>시작된 프로세스. stdout/stderr 리다이렉트 활성.</returns>
    public static Process Launch(LoadTestOptions coordinatorOptions, int workerIndex, int shardCount, int shardOffset, string outRoot)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // 현재 실행 파일 재기동: `dotnet LoadTester.dll` 또는 apphost(LoadTester.exe) 두 경우를 모두 처리한다.
        // Environment.ProcessPath가 dotnet 호스트면 엔트리 DLL을 첫 인자로 넘겨야 한다.
        string host = Environment.ProcessPath
            ?? throw new InvalidOperationException("Environment.ProcessPath를 확인할 수 없어 워커를 스폰할 수 없습니다.");
        string hostName = Path.GetFileNameWithoutExtension(host);
        psi.FileName = host;
        if (string.Equals(hostName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string entryDll = Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("엔트리 어셈블리 경로를 확인할 수 없습니다.");
            psi.ArgumentList.Add(entryDll);
        }

        // 워커 인자: 공유 옵션 verbatim + 샤드 오버라이드. 텔레메트리·서버 리소스는 코디네이터만.
        void Add(string k, string v) { psi.ArgumentList.Add(k); psi.ArgumentList.Add(v); }
        string Inv(double seconds) => ((int)seconds).ToString(CultureInfo.InvariantCulture) + "s";

        psi.ArgumentList.Add("--role"); psi.ArgumentList.Add("worker");
        Add("--worker-index", workerIndex.ToString(CultureInfo.InvariantCulture));
        Add("--clients", shardCount.ToString(CultureInfo.InvariantCulture));
        Add("--client-index-offset", shardOffset.ToString(CultureInfo.InvariantCulture));
        Add("--workers", coordinatorOptions.Workers.ToString(CultureInfo.InvariantCulture));
        Add("--port-count", coordinatorOptions.PortCount.ToString(CultureInfo.InvariantCulture));
        Add("--source-port-base", coordinatorOptions.SourcePortBase.ToString(CultureInfo.InvariantCulture));
        Add("--mode", coordinatorOptions.Mode);
        Add("--host", coordinatorOptions.Host);
        Add("--game-port", coordinatorOptions.GamePort.ToString(CultureInfo.InvariantCulture));
        Add("--auth-port", coordinatorOptions.AuthPort.ToString(CultureInfo.InvariantCulture));
        // 램프는 전역 속도를 K로 나눠 각 워커에 배분(합이 원래 속도).
        int workerRamp = Math.Max(1, coordinatorOptions.RampUpPerSecond / coordinatorOptions.Workers);
        Add("--ramp-up", workerRamp.ToString(CultureInfo.InvariantCulture));
        Add("--duration", Inv(coordinatorOptions.Duration.TotalSeconds));
        Add("--ping-interval", Inv(coordinatorOptions.PingInterval.TotalSeconds));
        Add("--auth-timeout", Inv(coordinatorOptions.AuthTimeout.TotalSeconds));
        Add("--report-interval", Inv(coordinatorOptions.ReportInterval.TotalSeconds));
        Add("--stall-timeout", Inv(coordinatorOptions.StallTimeout.TotalSeconds));
        Add("--accounts", coordinatorOptions.Accounts.ToString(CultureInfo.InvariantCulture));
        Add("--token-ttl", Inv(coordinatorOptions.TokenTtl.TotalSeconds));
        Add("--login-concurrency", coordinatorOptions.LoginConcurrency.ToString(CultureInfo.InvariantCulture));
        Add("--out", Path.Combine(outRoot, $"worker-{workerIndex}"));
        Add("--max-log-mb", coordinatorOptions.MaxLogMb.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--no-telemetry");
        // churn 스트레스: 워커가 접속→인증→즉시 종료→재접속을 반복하도록 플래그 전달.
        if (coordinatorOptions.Churn)
            psi.ArgumentList.Add("--churn");

        // 8워커 × 32 서버-GC 힙 과대커밋 방지: 워커당 GC 힙 수를 제한.
        psi.Environment["DOTNET_GCHeapCount"] = "4";

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        return process;
    }
}
