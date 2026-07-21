namespace LoadTester.Metrics;

/// <summary>
/// 전 가상 클라이언트가 공유하는 카운터 집합입니다. 발생 빈도에 따라 두 계층으로 나뉜다 —
/// 브로드캐스트/수신 바이트(초당 수만 회)는 <see cref="StripedLongCounter"/>, 연결·인증·끊김
/// (클라이언트당 저빈도)은 단일 필드 Interlocked. 이 구분은 카운터별 경합 빈도가 캐시라인
/// 핑퐁 비용을 정당화하는지에 따른 것이다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 모든 기록 메서드는 어느 스레드
/// (I/O 콜백 포함)에서든 동시 호출 가능하다.</description></item>
/// <item><description><b>Memory Allocation:</b> 기록 경로 무할당. <see cref="SnapshotTotals"/>는
/// record struct 반환(스택).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. lock-free 원자 연산만 사용.</description></item>
/// </list>
/// </remarks>
public sealed class MetricsAggregator
{
    /// <summary>수신한 레이드 브로드캐스트 패킷 수(MobHp/MobDeath). 핫 카운터.</summary>
    public StripedLongCounter Broadcasts { get; } = new();

    /// <summary>수신 총 바이트(앱 패킷 기준). 핫 카운터.</summary>
    public StripedLongCounter BytesIn { get; } = new();

    // long + Interlocked: 연결/인증/끊김은 클라이언트당 세션 수명에 한 번꼴인 저빈도 이벤트라
    // 스트라이프 없이 단일 필드 CAS로 충분하다(경합 확률이 캐시라인 분리 비용을 정당화하지 못함).
    private long _connectAttempts;
    private long _connectFailures;
    private long _authSuccesses;
    private long _authFailures;
    private long _authTimeouts;
    private long _loginFailures;
    private long _unexpectedDisconnects;
    private long _reconnects;

    /// <summary>연결 시도 1건 기록.</summary>
    public void RecordConnectAttempt() => Interlocked.Increment(ref _connectAttempts);

    /// <summary>연결 실패 1건 기록.</summary>
    public void RecordConnectFailure() => Interlocked.Increment(ref _connectFailures);

    /// <summary>인증 성공 1건 기록.</summary>
    public void RecordAuthSuccess() => Interlocked.Increment(ref _authSuccesses);

    /// <summary>인증 거부(ACK Success=false) 1건 기록.</summary>
    public void RecordAuthFailure() => Interlocked.Increment(ref _authFailures);

    /// <summary>인증 ACK 타임아웃 1건 기록.</summary>
    public void RecordAuthTimeout() => Interlocked.Increment(ref _authTimeouts);

    /// <summary>토큰 획득 실패(full 모드 로그인 거부/실패) 1건 기록.</summary>
    public void RecordLoginFailure() => Interlocked.Increment(ref _loginFailures);

    /// <summary>예기치 않은 연결 끊김(셧다운 외) 1건 기록.</summary>
    public void RecordUnexpectedDisconnect() => Interlocked.Increment(ref _unexpectedDisconnects);

    /// <summary>재접속 시도 진입 1건 기록.</summary>
    public void RecordReconnect() => Interlocked.Increment(ref _reconnects);

    /// <summary>모든 누적 카운터의 스냅샷을 반환합니다(필드 간 원자성은 없음 — 표시용 근사).</summary>
    public CounterTotals SnapshotTotals() => new(
        Interlocked.Read(ref _connectAttempts),
        Interlocked.Read(ref _connectFailures),
        Interlocked.Read(ref _authSuccesses),
        Interlocked.Read(ref _authFailures),
        Interlocked.Read(ref _authTimeouts),
        Interlocked.Read(ref _loginFailures),
        Interlocked.Read(ref _unexpectedDisconnects),
        Interlocked.Read(ref _reconnects),
        Broadcasts.Sum(),
        BytesIn.Sum());
}

/// <summary>누적 카운터 스냅샷입니다.</summary>
public readonly record struct CounterTotals(
    long ConnectAttempts,
    long ConnectFailures,
    long AuthSuccesses,
    long AuthFailures,
    long AuthTimeouts,
    long LoginFailures,
    long UnexpectedDisconnects,
    long Reconnects,
    long Broadcasts,
    long BytesIn)
{
    /// <summary>판정 규칙 ②의 분자: 실패 합계.</summary>
    public long TotalFailures => ConnectFailures + AuthFailures + AuthTimeouts + LoginFailures;

    /// <summary>판정 규칙 ②의 분모: 시도 합계(연결 시도 + 토큰 획득 실패는 연결 전 실패라 별도 합산).</summary>
    public long TotalAttempts => ConnectAttempts + LoginFailures;
}
