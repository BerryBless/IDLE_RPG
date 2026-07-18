using System.Buffers.Binary;
using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace MonitorServer;

/// <summary>
/// GameServer의 텔레메트리 리스너(포트 7779)에 ServerLib 클라이언트로 계속 재접속을 시도하며,
/// 도착하는 <see cref="TelemetrySnapshotPacket"/>을 <see cref="TelemetrySnapshotStore"/>에 반영하는
/// 백그라운드 루프입니다. GameServer가 아직 기동하지 않았거나 재시작되어도 자동으로 복구합니다.
/// </summary>
public static class TelemetryClientLoop
{
    // BinaryPacketSerializer: 무상태(내부 가변 필드 없음)라 여러 재접속 사이클에 걸쳐 단일 인스턴스를
    // 재사용해도 안전하다(GameServer의 RaidBroadcaster._serializer와 동일 근거).
    private static readonly BinaryPacketSerializer Serializer = new();

    /// <summary>연결 → 수신 → (끊김 감지 시) 지연 후 재접속을 서버 수명 동안 반복합니다.</summary>
    /// <param name="host">GameServer 텔레메트리 리스너 호스트(루프백 고정 운용 전제)</param>
    /// <param name="port">GameServer 텔레메트리 리스너 포트. <c>GameServer/Main.cs</c>의 <c>TelemetryPort</c>와 반드시 일치해야 한다.</param>
    /// <param name="store">수신한 스냅샷을 반영할 홀더</param>
    /// <param name="reconnectDelay">연결 실패 또는 끊김 이후 다음 재접속 시도까지의 대기 시간</param>
    /// <param name="lifetimeToken">프로세스 종료 시 취소되는 수명 토큰</param>
    /// <remarks>
    /// <b>Blocking 여부:</b> 이 태스크 자체는 호출자가 <c>Task.Run</c> 등으로 fire-and-forget 실행해야
    /// 한다(무한 루프, 정상적으로는 <paramref name="lifetimeToken"/> 취소로만 반환).
    /// <br/><br/>
    /// <b>Thread Context:</b> <see cref="IClientConnection.OnReceived"/>는 ServerLib의 I/O 스레드
    /// 풀에서 직접 호출되므로 그 안에서 동기 블로킹을 하지 않는다 — 즉시 역직렬화 후
    /// <paramref name="store"/> 갱신만 수행하고 반환한다.
    /// </remarks>
    public static async Task RunAsync(string host, int port, TelemetrySnapshotStore store,
        TimeSpan reconnectDelay, CancellationToken lifetimeToken)
    {
        while (!lifetimeToken.IsCancellationRequested)
        {
            // await using: IClientConnection.OnConnected/OnReceived/OnDisconnected는 ConnectAsync()
            // 호출 전에만 설정 가능하고 이후 재설정은 InvalidOperationException이므로(IClientConnection
            // 계약), 재접속마다 반드시 새 인스턴스를 생성해야 한다 — 이전 인스턴스는 재사용할 수 없다.
            // 블록(while 바디) 끝에서 DisposeAsync가 호출돼 다음 반복 전에 소켓 자원을 정리한다.
            await using IClientConnection client = ServerNet.CreateClient();

            // TaskCompletionSource(RunContinuationsAsynchronously): OnDisconnected 콜백(I/O 스레드)이
            // 신호를 보내면 이 루프(호출 스레드)가 깨어나 재접속을 시도한다 — 콜백과 루프 사이의 유일한
            // 통신 수단. RunContinuationsAsynchronously로 콜백을 호출한 I/O 스레드에서 곧바로 이어지는
            // 연속 작업(재접속 로직)이 동기 실행되어 I/O 스레드를 점유하는 것을 방지한다.
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            client.OnReceived = data =>
            {
                // ReadOnlyMemory<byte>는 수신 파이프 버퍼의 슬라이스 뷰 — 콜백 반환 후 무효화되므로
                // 콜백 안에서 동기적으로 역직렬화까지 끝낸다(SessionRaidRunnerEndToEndTests의 클라이언트
                // 측 패킷 라우팅과 동일 패턴: 헤더 2바이트로 PacketId를 먼저 읽어 분기).
                ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
                if (packetId == TelemetrySnapshotPacket.Id)
                {
                    var pkt = Serializer.Deserialize<TelemetrySnapshotPacket>(data.Span);
                    store.Update(new MonitorSnapshot(
                        Connected: true,
                        UpdatedAtUtc: DateTime.UtcNow,
                        ConnectedCount: pkt.ConnectedCount,
                        IsRunning: pkt.IsRunning,
                        RejectedConnections: pkt.RejectedConnections,
                        BossCurrentHp: pkt.BossCurrentHp,
                        BossMaxHp: pkt.BossMaxHp,
                        Generation: pkt.Generation,
                        LastEvent: pkt.LastEvent,
                        TopDamage: pkt.TopDamage,
                        MvpName: pkt.MvpName));
                }
                return ValueTask.CompletedTask;
            };
            client.OnDisconnected = () =>
            {
                disconnected.TrySetResult();
                return ValueTask.CompletedTask;
            };

            try
            {
                await client.ConnectAsync(host, port, lifetimeToken);
                await disconnected.Task.WaitAsync(lifetimeToken);
            }
            catch (OperationCanceledException)
            {
                break; // 프로세스 종료
            }
            catch (Exception)
            {
                // 연결 실패(GameServer 미기동·거부 등) 또는 그 밖의 소켓 오류 — 아래 공통 경로에서 재시도.
                // 원인별로 분기하지 않는다: 어떤 이유로 끊기든 대응은 동일(재접속 대기 후 재시도)하기 때문.
            }

            store.MarkDisconnected();

            try
            {
                await Task.Delay(reconnectDelay, lifetimeToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
