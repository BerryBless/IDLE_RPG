using System.Buffers.Binary;
using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace LoadTester.Telemetry;

/// <summary>수신한 서버 텔레메트리의 불변 스냅샷입니다(없으면 null 참조 자체로 미수신 표현).</summary>
/// <param name="ReceivedAtUtc">수신 시각.</param>
/// <param name="ConnectedCount">서버가 보고한 접속 세션 수(툴 active와 교차 검증).</param>
/// <param name="RejectedConnections">서버 누적 거부 연결 수.</param>
/// <param name="Generation">현재 레이드 세대.</param>
/// <param name="BossCurrentHp">보스 현재 HP.</param>
/// <param name="BossMaxHp">보스 최대 HP.</param>
public sealed record TelemetrySample(
    DateTime ReceivedAtUtc, int ConnectedCount, long RejectedConnections,
    int Generation, long BossCurrentHp, long BossMaxHp);

/// <summary>
/// GameServer 텔레메트리 리스너(기본 7779, 무인증)를 구독해 최신 <see cref="TelemetrySample"/>을
/// 유지하는 백그라운드 루프입니다. 끊기면 자동 재접속한다(MonitorServer TelemetryClientLoop 패턴).
/// 툴이 측정한 접속 수(active)와 서버가 보고한 ConnectedCount의 독립 교차 검증에 쓰인다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="Latest"/>는 volatile 참조
/// 교체로 발행된다 — 리더(샘플러)는 락 없이 최신 스냅샷을 읽는다.</description></item>
/// <item><description><b>Memory Allocation:</b> 스냅샷 수신마다 record 1개(1초 주기 — 무해).</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. <see cref="RunAsync"/>는 fire-and-forget
/// 태스크로 실행해야 한다(취소로만 반환).</description></item>
/// </list>
/// </remarks>
public sealed class TelemetrySubscriber
{
    private static readonly BinaryPacketSerializer Serializer = new();
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(3);

    // volatile 참조 교체: "최신 값 하나"만 필요한 단일 라이터(수신 콜백)-다중 리더(샘플러) 패턴.
    // record가 불변이라 참조 교체만 원자화하면 찢어진 읽기가 원천 불가능하다(TelemetrySnapshotStore와 동일 근거).
    private volatile TelemetrySample? _latest;

    /// <summary>마지막으로 수신한 텔레메트리 스냅샷. 아직 없으면 null.</summary>
    public TelemetrySample? Latest => _latest;

    /// <summary>연결 → 수신 → 끊김 시 재접속을 수명 동안 반복합니다.</summary>
    /// <param name="host">텔레메트리 리스너 호스트.</param>
    /// <param name="port">텔레메트리 리스너 포트.</param>
    /// <param name="lifetime">실행 수명 토큰.</param>
    public async Task RunAsync(string host, int port, CancellationToken lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            // await using: 콜백 재설정 불가 계약상 재접속마다 새 인스턴스(TelemetryClientLoop와 동일).
            await using IClientConnection client = ServerNet.CreateClient();

            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            client.OnReceived = data =>
            {
                ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
                if (packetId == TelemetrySnapshotPacket.Id)
                {
                    var pkt = Serializer.Deserialize<TelemetrySnapshotPacket>(data.Span);
                    _latest = new TelemetrySample(
                        DateTime.UtcNow, pkt.ConnectedCount, pkt.RejectedConnections,
                        pkt.Generation, pkt.BossCurrentHp, pkt.BossMaxHp);
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
                await client.ConnectAsync(host, port, lifetime);
                await disconnected.Task.WaitAsync(lifetime);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 연결 실패/소켓 오류 — 원인 무관 공통 재시도 경로.
            }

            try
            {
                await Task.Delay(ReconnectDelay, lifetime);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
