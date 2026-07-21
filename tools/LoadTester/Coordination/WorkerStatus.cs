using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoadTester.Coordination;

/// <summary>워커가 코디네이터에 stdout으로 보고하는 구간 스냅샷입니다(<c>@interval</c> 라인).</summary>
/// <param name="WorkerIndex">워커 인덱스.</param>
/// <param name="ElapsedSec">워커 실행 경과 초.</param>
/// <param name="Active">이 워커의 현재 연결 수.</param>
/// <param name="Target">이 워커의 목표 클라이언트 수(샤드).</param>
/// <param name="Authenticated">이 워커의 현재 인증 완료 수.</param>
/// <param name="ConnectAttempts">누적 연결 시도.</param>
/// <param name="TotalFailures">누적 실패(연결+인증+타임아웃+로그인).</param>
/// <param name="UnexpectedDisconnects">누적 예기치 않은 끊김.</param>
/// <param name="Reconnects">누적 재접속.</param>
/// <param name="StalledClients">현재 스톨 클라이언트 수.</param>
/// <param name="SelfWorkingSetMb">워커 자신의 워킹셋(MB) — 워커별 메모리 누수 감시.</param>
public sealed record WorkerStatus(
    int WorkerIndex, double ElapsedSec, int Active, int Target, int Authenticated,
    long ConnectAttempts, long TotalFailures, long UnexpectedDisconnects, long Reconnects,
    int StalledClients, double SelfWorkingSetMb);

/// <summary>워커가 종료 시 stdout으로 보고하는 최종 요약입니다(<c>@final</c> 라인).</summary>
/// <param name="WorkerIndex">워커 인덱스.</param>
/// <param name="Passed">워커 로컬 판정(참고용 — 최종 판정은 코디네이터).</param>
/// <param name="PeakAuthenticated">이 워커가 관측한 최대 동시 인증 수.</param>
/// <param name="ConnectAttempts">최종 누적 연결 시도.</param>
/// <param name="TotalFailures">최종 누적 실패.</param>
/// <param name="ExitReason">종료 사유(normal|aborted 등, 진단용).</param>
public sealed record WorkerFinal(
    int WorkerIndex, bool Passed, int PeakAuthenticated,
    long ConnectAttempts, long TotalFailures, string ExitReason);

// STJ 소스 생성: 런타임 리플렉션 없이 컴파일 시점 코드로 직렬화(GameEventJsonContext와 동일 근거).
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(WorkerStatus))]
[JsonSerializable(typeof(WorkerFinal))]
internal partial class WorkerJsonContext : JsonSerializerContext
{
}

/// <summary>
/// 워커→코디네이터 stdout 라인 프로토콜의 직렬화/파싱 헬퍼입니다.
/// 기계 라인은 <c>@interval {json}</c> / <c>@final {json}</c> 접두사로 사람이 읽는 로그 라인과 구분됩니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe(무상태 정적). <b>[Blocking:]</b> Non-blocking.
/// </remarks>
public static class WorkerLineProtocol
{
    /// <summary>구간 라인 접두사.</summary>
    public const string IntervalPrefix = "@interval ";

    /// <summary>최종 라인 접두사.</summary>
    public const string FinalPrefix = "@final ";

    /// <summary><see cref="WorkerStatus"/>를 <c>@interval {json}</c> 라인으로 직렬화합니다.</summary>
    public static string FormatInterval(WorkerStatus status) =>
        IntervalPrefix + JsonSerializer.Serialize(status, WorkerJsonContext.Default.WorkerStatus);

    /// <summary><see cref="WorkerFinal"/>을 <c>@final {json}</c> 라인으로 직렬화합니다.</summary>
    public static string FormatFinal(WorkerFinal final) =>
        FinalPrefix + JsonSerializer.Serialize(final, WorkerJsonContext.Default.WorkerFinal);

    /// <summary>한 줄을 파싱해 <see cref="WorkerStatus"/>를 얻습니다. 구간 라인이 아니거나 손상 시 false.</summary>
    public static bool TryParseInterval(string line, out WorkerStatus? status)
    {
        status = null;
        if (line is null || !line.StartsWith(IntervalPrefix, StringComparison.Ordinal))
            return false;
        try
        {
            status = JsonSerializer.Deserialize(line.AsSpan(IntervalPrefix.Length), WorkerJsonContext.Default.WorkerStatus);
            return status is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>한 줄을 파싱해 <see cref="WorkerFinal"/>을 얻습니다. 최종 라인이 아니거나 손상 시 false.</summary>
    public static bool TryParseFinal(string line, out WorkerFinal? final)
    {
        final = null;
        if (line is null || !line.StartsWith(FinalPrefix, StringComparison.Ordinal))
            return false;
        try
        {
            final = JsonSerializer.Deserialize(line.AsSpan(FinalPrefix.Length), WorkerJsonContext.Default.WorkerFinal);
            return final is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
