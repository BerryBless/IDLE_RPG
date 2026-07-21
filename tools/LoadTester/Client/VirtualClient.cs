using System.Buffers.Binary;
using LoadTester.Auth;
using LoadTester.Metrics;
using LoadTester.Options;
using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace LoadTester.Client;

/// <summary>샘플러가 주기적으로 읽는 가상 클라이언트 1개의 상태 스냅샷입니다.</summary>
/// <param name="Connected">현재 소켓 연결 상태.</param>
/// <param name="Authenticated">현재 인증 완료 상태.</param>
/// <param name="RttTicks">마지막 측정 RTT(TimeSpan tick). 미측정 시 0.</param>
/// <param name="LastAppPacketUnixMs">마지막 앱 패킷(ACK/브로드캐스트) 수신 시각(Unix ms). 스톨 판정 기준.</param>
/// <param name="EverAuthenticated">이 클라이언트가 실행 중 한 번이라도 인증에 성공했는지(판정 규칙 ②).</param>
public readonly record struct VirtualClientSnapshot(
    bool Connected, bool Authenticated, long RttTicks, long LastAppPacketUnixMs, bool EverAuthenticated);

/// <summary>
/// 가상 클라이언트 1개의 연결 수명 주기 상태머신입니다:
/// 토큰 획득 → (페이서 허가) 연결 → AuthTokenPacket 송신 → ACK 대기 → 수신 유지 →
/// 끊김 분류 → 지수 백오프 → 반복. 실행 내내 <see cref="RunAsync"/> 태스크 1개가 전담한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <see cref="RunAsync"/>는 단일 태스크 전용.
/// <see cref="ReadSnapshot"/>만 다른 스레드(샘플러)에서 동시 호출 가능하다 —
/// 내부 상태는 Volatile로 발행된다.</description></item>
/// <item><description><b>Memory Allocation:</b> 연결 사이클당 클라이언트·TCS 등 소량 할당
/// (IClientConnection 콜백 재설정 불가 계약상 재접속마다 새 인스턴스 필수).
/// OnReceived 콜백 자체는 무할당 경로(패킷 ID 스위치 + 카운터 + Volatile.Write).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 모든 대기는 비동기.</description></item>
/// </list>
/// </remarks>
public sealed class VirtualClient
{
    // BinaryPacketSerializer: 무상태라 전 클라이언트가 단일 인스턴스 공유(TelemetryClientLoop와 동일 근거).
    private static readonly BinaryPacketSerializer Serializer = new();

    private readonly int _index;
    private readonly LoadTestOptions _options;
    private readonly ITokenSource _tokens;
    private readonly MetricsAggregator _metrics;
    private readonly ReconnectPolicy _reconnect;
    private readonly ConnectPacer _pacer;

    // volatile 참조/필드: RunAsync 태스크(라이터)와 샘플러 스레드(리더) 사이에 절반 갱신 값이
    // 보이지 않도록 발행한다. CAS 누적이 필요 없는 "최신 값 하나" 패턴이라 Interlocked 불필요.
    private volatile IClientConnection? _activeClient;
    private volatile bool _authenticated;
    private volatile bool _everAuthenticated;
    private long _lastAppPacketUnixMs;

    /// <summary>가상 클라이언트를 생성합니다.</summary>
    /// <param name="index">클라이언트 인덱스(0부터). 계정 매핑에 쓰인다.</param>
    /// <param name="options">실행 옵션.</param>
    /// <param name="tokens">토큰 획득 전략.</param>
    /// <param name="metrics">공유 카운터.</param>
    /// <param name="reconnect">재접속 백오프 정책(이 클라이언트 전용 인스턴스).</param>
    /// <param name="pacer">공유 연결 페이서.</param>
    public VirtualClient(int index, LoadTestOptions options, ITokenSource tokens,
        MetricsAggregator metrics, ReconnectPolicy reconnect, ConnectPacer pacer)
    {
        _index = index;
        _options = options;
        _tokens = tokens;
        _metrics = metrics;
        _reconnect = reconnect;
        _pacer = pacer;
    }

    /// <summary>샘플러 스레드가 호출하는 상태 스냅샷 읽기입니다(무할당, non-blocking).</summary>
    public VirtualClientSnapshot ReadSnapshot()
    {
        IClientConnection? client = _activeClient;
        return new VirtualClientSnapshot(
            Connected: client?.IsConnected ?? false,
            Authenticated: _authenticated,
            RttTicks: client?.Rtt.Ticks ?? 0,
            LastAppPacketUnixMs: Interlocked.Read(ref _lastAppPacketUnixMs),
            EverAuthenticated: _everAuthenticated);
    }

    /// <summary>취소될 때까지 연결 수명 주기를 반복합니다. 예외는 내부에서 통계로 흡수한다.</summary>
    /// <param name="lifetime">전체 실행 수명 토큰. 취소 시 정상 종료.</param>
    public async Task RunAsync(CancellationToken lifetime)
    {
        int failureStreak = 0;
        bool isReconnect = false;

        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                if (isReconnect)
                    _metrics.RecordReconnect();

                bool cycleSucceeded = await RunConnectionCycleAsync(lifetime);
                isReconnect = true;

                if (cycleSucceeded)
                {
                    failureStreak = 0;
                    // 인증까지 갔던 세션의 끊김: 백오프 1회차부터 다시 시작.
                }
                failureStreak++;
                await Task.Delay(_reconnect.NextDelay(failureStreak), lifetime);
            }
            catch (OperationCanceledException)
            {
                break; // 정상 셧다운
            }
        }
    }

    /// <summary>연결 1사이클을 수행합니다. 인증 성공 후 끊김이면 true, 그 전 실패면 false.</summary>
    private async Task<bool> RunConnectionCycleAsync(CancellationToken lifetime)
    {
        TokenResult token = await _tokens.AcquireAsync(_index, lifetime);
        if (!token.Success)
        {
            _metrics.RecordLoginFailure();
            return false;
        }

        await _pacer.WaitAsync(lifetime);

        // await using: 콜백은 ConnectAsync 전에만 설정 가능(IClientConnection 계약)이라
        // 사이클마다 새 인스턴스 — 블록 종료 시 DisposeAsync로 소켓 자원 정리.
        await using IClientConnection client = ServerNet.CreateClient();
        client.PingInterval = _options.PingInterval;
        client.SendTimeout = TimeSpan.FromSeconds(5);

        // TaskCompletionSource(RunContinuationsAsynchronously): I/O 콜백 스레드가 신호를 보내고
        // 이 태스크가 깨어난다. 연속 작업(재접속 로직)이 I/O 스레드를 점유하지 않게 비동기 재개.
        var ackReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        client.OnReceived = data =>
        {
            // I/O 스레드 직접 호출 경로: 패킷 ID 분기 + 카운터 + 시각 발행만 수행(무블로킹·무할당).
            // ReadOnlyMemory는 반환 후 무효 — 보관하지 않는다.
            ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
            switch (packetId)
            {
                case AuthTokenAckPacket.Id:
                    Interlocked.Exchange(ref _lastAppPacketUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    var ack = Serializer.Deserialize<AuthTokenAckPacket>(data.Span); // 1바이트 struct — 무할당
                    ackReceived.TrySetResult(ack.Success);
                    break;

                case MobHpPacket.Id:
                case MobDeathPacket.Id:
                    Interlocked.Exchange(ref _lastAppPacketUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    _metrics.Broadcasts.Increment();
                    _metrics.BytesIn.Add(data.Length);
                    break;
            }
            return ValueTask.CompletedTask;
        };
        client.OnDisconnected = () =>
        {
            disconnected.TrySetResult();
            return ValueTask.CompletedTask;
        };

        _metrics.RecordConnectAttempt();
        try
        {
            await client.ConnectAsync(_options.Host, _options.GamePort, lifetime);
        }
        catch (OperationCanceledException)
        {
            _pacer.Release();
            throw;
        }
        catch (Exception)
        {
            _pacer.Release();
            _metrics.RecordConnectFailure();
            return false;
        }
        _pacer.Release();

        _activeClient = client;
        try
        {
            await client.SendAsync(new AuthTokenPacket { Token = token.Token! }, lifetime);

            bool authOk;
            try
            {
                authOk = await ackReceived.Task.WaitAsync(_options.AuthTimeout, lifetime);
            }
            catch (TimeoutException)
            {
                _metrics.RecordAuthTimeout();
                return false;
            }

            if (!authOk)
            {
                _metrics.RecordAuthFailure();
                return false;
            }

            _metrics.RecordAuthSuccess();
            _authenticated = true;
            _everAuthenticated = true;

            // 인증 직후를 수신 기준점으로: 첫 브로드캐스트 도착 전 구간이 스톨로 오판되지 않게 한다.
            Interlocked.Exchange(ref _lastAppPacketUnixMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            // 끊김 또는 셧다운까지 유지. 셧다운이면 OCE로 빠져 RunAsync에서 정상 종료.
            await disconnected.Task.WaitAsync(lifetime);

            // 여기 도달 = 서버측/네트워크 원인 끊김(셧다운 아님).
            _metrics.RecordUnexpectedDisconnect();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 송신 실패 등 — 연결은 됐으나 세션 확립 실패로 분류.
            _metrics.RecordConnectFailure();
            return false;
        }
        finally
        {
            _authenticated = false;
            _activeClient = null;
        }
    }
}
