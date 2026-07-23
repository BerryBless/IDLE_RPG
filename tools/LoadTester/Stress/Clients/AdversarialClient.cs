using ServerLib;
using ServerLib.Interface;

namespace LoadTester.Stress.Clients;

/// <summary>
/// 악성 프레임을 서버 인증 게이트에 쏟아붓는 적대적 클라이언트입니다. 정상 클라이언트와 달리
/// <see cref="IClientConnection.SendAsync(ReadOnlyMemory{byte}, CancellationToken)"/> <b>원시 오버로드</b>로
/// 프레이밍 없이 잘못된 바이트를 직접 전송합니다(프레임 래핑 확장 메서드를 쓰지 않음). 플러드 후에도
/// 연결을 유지해 서버측 세션 누적을 함께 측정합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> <see cref="RunAsync"/> 단일 태스크. 카운터는 Interlocked. <b>[Blocking:]</b> Non-blocking.
/// </remarks>
public sealed class AdversarialClient
{
    private readonly int _index;
    private readonly string _host;
    private readonly int _port;
    private readonly Random _rng;

    private long _framesSent;
    private volatile bool _connected;

    /// <summary>적대적 클라이언트를 생성합니다.</summary>
    public AdversarialClient(int index, string host, int port)
    {
        _index = index;
        _host = host;
        _port = port;
        // 인덱스 파생 시드(결정적·워커별 상이). Random.Shared 대신 전용 인스턴스(스레드 안전 아님).
        _rng = new Random(HashCode.Combine(index, 0x9E3779B9));
    }

    /// <summary>이 클라이언트가 전송한 악성 프레임 수.</summary>
    public long FramesSent => Interlocked.Read(ref _framesSent);

    /// <summary>현재 연결 상태.</summary>
    public bool IsConnected => _connected;

    /// <summary>연결 후 악성 프레임을 회전 전송하고 취소까지 연결을 유지합니다.</summary>
    public async Task RunAsync(CancellationToken lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                // await using: 콜백 재설정 불가 계약상 사이클마다 새 인스턴스.
                await using IClientConnection client = ServerNet.CreateClient();
                // 수신 콜백은 두지 않는다(ack-false가 와도 무시) — 서버가 종료하지 않고 유지하는지가 관심사.
                client.OnDisconnected = () => { _connected = false; return ValueTask.CompletedTask; };

                await client.ConnectAsync(_host, _port, lifetime);
                _connected = true;

                // 회전 악성 프레임 플러드. 각 변종이 서로 다른 리더/게이트 경로를 자극한다.
                int cycle = 0;
                while (!lifetime.IsCancellationRequested && client.IsConnected)
                {
                    byte[] frame = NextMalformedFrame(cycle++);
                    await client.SendAsync(frame, lifetime); // 원시 오버로드 — 프레이밍 없이 verbatim
                    Interlocked.Increment(ref _framesSent);

                    // 플러드지만 CPU 100% 점유는 피한다 — 소량 양보(서버 붕괴 여부가 관심사, DoS 자체 최적화 아님).
                    if ((cycle & 0x3F) == 0)
                        await Task.Delay(1, lifetime);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                _connected = false;
                // 서버가 이 세션 송신을 끊었을 수 있다 — 재접속해 계속 압박.
                try { await Task.Delay(50, lifetime); } catch (OperationCanceledException) { break; }
            }
        }
        _connected = false;
    }

    private byte[] NextMalformedFrame(int cycle) => (cycle % 7) switch
    {
        0 => MalformedFrames.RandomGarbage(_rng.Next(1, 64), _rng),
        1 => MalformedFrames.OversizedLengthHeader(),
        2 => MalformedFrames.TruncatedFrame(claimedBodyLength: 200, actualBodyBytes: _rng.Next(0, 20)),
        3 => MalformedFrames.WrongPacketId((ushort)_rng.Next(100, 60000)),
        4 => MalformedFrames.ValidFrameGarbageBody(_rng.Next(1, 128), _rng),
        5 => MalformedFrames.ZeroLengthBody(),
        _ => MalformedFrames.PartialHeader(),
    };
}
