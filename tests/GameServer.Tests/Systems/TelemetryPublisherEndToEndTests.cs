using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Systems;
using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace GameServer.Tests.Systems;

/// <summary>
/// <c>GameServer/Main.cs</c>의 텔레메트리 리스너 배선(<see cref="TelemetryPublisher"/>가 게임 리스너
/// 통계 + <see cref="RaidEncounter"/> onStep을 <see cref="TelemetrySnapshotPacket"/>으로 조립해
/// 텔레메트리 전용 <see cref="ISessionRegistry"/>에 브로드캐스트)을 실제 루프백 TCP 소켓으로
/// End-to-End 검증한다. <see cref="SessionRaidRunnerEndToEndTests"/>와 동일하게 두 모니터 클라이언트가
/// **정확히 같은 스냅샷 바이트**를 수신하는지가 핵심 주장이다(공유 브로드캐스트 실증).
/// </summary>
public class TelemetryPublisherEndToEndTests
{
    // TcpListener(Loopback, 0): OS가 미사용 임시 포트를 배정 → 병렬 테스트 간 포트 충돌 방지(SessionRaidRunnerEndToEndTests와 동일 헬퍼).
    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task TwoConcurrentMonitorConnections_BothReceiveByteIdenticalSnapshot_ReflectingBossStep()
    {
        const int TimeoutMs = 5_000;

        var serializer = new BinaryPacketSerializer();

        // 게임 리스너: TelemetryPublisher가 ActiveSessionCount/IsRunning/TotalRejectedConnections를
        // 읽는 대상. 이 테스트는 게임 클라이언트를 실제로 접속시키지 않으므로 ActiveSessionCount==0을
        // 그대로 스냅샷에서 검증한다(Main.cs와 동일하게 game registry 없이도 IServerListener 자체의
        // 통계 속성만으로 충분 — TelemetryPublisher 생성자 시그니처 참고).
        int gamePort = GetFreePort();
        IServerListener gameListener = ServerNet.CreateListener();
        gameListener.Start(gamePort, IPAddress.Loopback);

        var telemetryRegistry = ServerNet.CreateSessionRegistry();
        int telemetryPort = GetFreePort();
        IServerListener telemetryListener = ServerNet.CreateListener(telemetryRegistry);
        telemetryListener.Start(telemetryPort, IPAddress.Loopback);

        var publisher = new TelemetryPublisher(gameListener, telemetryRegistry, publishInterval: TimeSpan.FromMilliseconds(30));

        using var lifetimeCts = new CancellationTokenSource();
        var publishLoopTask = Task.Run(() => publisher.PublishLoopAsync(lifetimeCts.Token));

        try
        {
            // 실제 RaidEncounter 액터 없이도 onStep 계약만으로 퍼블리셔를 구동한다 — TelemetryPublisher는
            // RaidEncounter를 직접 참조하지 않고 오직 RaidStepBroadcast 값에만 의존하므로 이 방식으로도
            // Main.cs의 실제 배선(RaidEncounter.RunAsync가 onStep을 호출)과 동일한 계약을 검증할 수 있다.
            await publisher.OnStep(
                new RaidStepBroadcast(RaidEventType.BossDamaged, CurrentHp: 4_200_000, MaxHp: 5_000_000,
                    DeadGeneration: 1, NewGeneration: 1, MvpName: string.Empty, TopDamage: 0),
                CancellationToken.None);

            var snapA = new TaskCompletionSource<TelemetrySnapshotPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var snapB = new TaskCompletionSource<TelemetrySnapshotPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rawA = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var rawB = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

            var clientA = ServerNet.CreateClient();
            clientA.OnReceived = data =>
            {
                ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
                if (packetId == TelemetrySnapshotPacket.Id)
                {
                    rawA.TrySetResult(data.ToArray());
                    snapA.TrySetResult(serializer.Deserialize<TelemetrySnapshotPacket>(data.Span));
                }
                return ValueTask.CompletedTask;
            };

            var clientB = ServerNet.CreateClient();
            clientB.OnReceived = data =>
            {
                ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
                if (packetId == TelemetrySnapshotPacket.Id)
                {
                    rawB.TrySetResult(data.ToArray());
                    snapB.TrySetResult(serializer.Deserialize<TelemetrySnapshotPacket>(data.Span));
                }
                return ValueTask.CompletedTask;
            };

            await Task.WhenAll(
                clientA.ConnectAsync("127.0.0.1", telemetryPort),
                clientB.ConnectAsync("127.0.0.1", telemetryPort));

            var receivedA = await snapA.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var receivedB = await snapB.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            // 게임 리스너 통계 반영 검증(게임 클라이언트 미접속이므로 0/가동중이어야 함).
            Assert.Equal(0, receivedA.ConnectedCount);
            Assert.True(receivedA.IsRunning);
            Assert.Equal(0, receivedA.RejectedConnections);

            // onStep으로 넣은 보스 값이 스냅샷에 정확히 반영됐는지 검증.
            Assert.Equal(4_200_000, receivedA.BossCurrentHp);
            Assert.Equal(5_000_000, receivedA.BossMaxHp);
            Assert.Equal(1, receivedA.Generation);
            Assert.Equal((byte)RaidEventType.BossDamaged, receivedA.LastEvent);

            // 핵심 주장: 두 모니터 클라이언트가 정확히 같은 브로드캐스트 바이트를 받는다(공유 스냅샷 실증).
            // TelemetrySnapshotPacket은 record가 아닌 sealed class(MobDeathPacket과 동일 패턴)라
            // 값 동등성이 없으므로, 원시 바이트 비교(가장 강한 증거) + 필드별 비교를 함께 확인한다.
            var rawBytesA = await rawA.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var rawBytesB = await rawB.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Equal(rawBytesA, rawBytesB);
            Assert.Equal(receivedA.BossCurrentHp, receivedB.BossCurrentHp);
            Assert.Equal(receivedA.BossMaxHp, receivedB.BossMaxHp);
            Assert.Equal(receivedA.Generation, receivedB.Generation);
            Assert.Equal(receivedA.ConnectedCount, receivedB.ConnectedCount);

            await clientA.DisposeAsync();
            await clientB.DisposeAsync();
        }
        finally
        {
            lifetimeCts.Cancel();
            try { await publishLoopTask; } catch (OperationCanceledException) { }
            gameListener.Stop();
            telemetryListener.Stop();
        }
    }
}
