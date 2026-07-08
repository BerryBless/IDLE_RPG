using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using GameServer.Items;
using GameServer.Stats;
using GameServer.Systems;
using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace GameServer.Tests.Systems;

/// <summary>
/// <c>Main.cs</c>의 공유 보스 co-op 배선(연결 시 <see cref="SessionRaidRunner"/>가 시작 장비를 착용시키고
/// 세션별 제출 루프를 시작, 보스 HP/처치를 <see cref="ISessionRegistry"/>로 전 세션에 브로드캐스트)을
/// 실제 루프백 TCP 소켓 2개로 End-to-End 검증합니다. 사이클 1의 격리 검증(각자 다른 몬스터)과 반대로,
/// 여기서는 **두 클라이언트가 정확히 같은 보스 상태를 공유**받는지가 핵심 주장이다.
/// </summary>
/// <remarks>
/// <c>Main.cs</c>는 top-level 문이라 테스트에서 직접 호출할 수 없습니다(사이클 1과 동일한 이유).
/// 대신 <see cref="SessionPlayerBinder"/> + <see cref="SessionRaidRunner"/>(둘 다 실제 프로덕션
/// 클래스)를 <see cref="ServerNet.CreateListener(ISessionRegistry?)"/> 콜백에 Main.cs와 동일한 순서로
/// 조합하고, 이번엔 <see cref="ServerNet.CreateSessionRegistry"/>도 함께 배선한다(브로드캐스트 필수 전제).
/// <para>
/// <b>[결정론 확보]</b> 기본 장비(무기 4001)로 실제 플레이어가 입히는 피해(대략 16.5/틱, 크리티컬
/// 없음 — 기본 <c>BaseTraits.CritProb=0</c>)를 그대로 쓰되, 보스 HP를 200으로 작게 잡은 커스텀
/// <see cref="MonsterTable"/>을 주입해 두 클라이언트가 접속을 마치기 전에 보스가 죽어버리는 레이스
/// 없이(최소 여러 틱 소요) 충분히 빠르게(5초 타임아웃 안에) 처치되도록 한다.
/// </para>
/// </remarks>
public class SessionRaidRunnerEndToEndTests
{
    // TcpListener(Loopback, 0): OS가 미사용 임시 포트를 배정 → 병렬 테스트 간 포트 충돌 방지.
    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>보스(7001) HP를 200으로 작게 잡은 커스텀 몬스터 테이블 — 기본 장비 피해로도 몇 틱 안에
    /// 처치되지만, 두 클라이언트가 접속을 마칠 시간은 충분히 남긴다(반격 없음, Atk=0).</summary>
    private static MonsterTable BuildSmallRaidBossTable() => new(new List<MonsterTemplate>
    {
        new()
        {
            MonsterId = 7001, Name = "테스트 레이드 보스", Level = 20,
            Hp = 200, Atk = 0, Def = 0,
            ExpDrop = 100, GoldDrop = 200,
            DropTable = []
        }
    });

    [Fact]
    public async Task TwoConcurrentConnections_BothReceiveSharedBossHpAndDeathBroadcast()
    {
        const int TimeoutMs = 5_000;

        var meterName = $"Test.SessionRaidRunner.{Guid.NewGuid()}";
        var metrics = new GameMetrics(meterName);
        await using var sink = new GameEventSink(new StringWriter(), metrics);

        var levelSystem = PlayerLevelSystem.CreateDefault();
        var binder = new SessionPlayerBinder(levelSystem, sink);

        // ServerNet.CreateSessionRegistry(): 브로드캐스트 대상(접속 세션 전체)을 추적하는 레지스트리 —
        // 반드시 CreateListener에도 같은 인스턴스를 넘겨야 리스너가 접속/해제를 자동 등록/해제한다.
        var registry = ServerNet.CreateSessionRegistry();
        var raidRunner = new SessionRaidRunner(
            levelSystem, BuildSmallRaidBossTable(), EquipmentTable.CreateDefault(), sink, registry,
            raidTimeLimit: TimeSpan.FromSeconds(30), tickInterval: TimeSpan.FromMilliseconds(5)); // 빠른 틱

        using var lifetimeCts = new CancellationTokenSource();
        raidRunner.Start(lifetimeCts.Token); // 액터 루프 + 드레인 루프 기동

        var serializer = new BinaryPacketSerializer();

        int port = GetFreePort();
        IServerListener listener = ServerNet.CreateListener(registry);

        // Main.cs와 동일한 순서로 조합: 연결 시 binder 먼저(Player를 Context에 부착) → raidRunner
        // (장비 착용 + 제출 루프 시작). 해제 시 반대 순서.
        listener.OnClientConnected = async session =>
        {
            await binder.OnConnected(session);
            await raidRunner.OnConnected(session);
        };
        listener.OnClientDisconnected = async session =>
        {
            await raidRunner.OnDisconnected(session);
            await binder.OnDisconnected(session);
        };
        listener.OnClientError = binder.OnError;

        listener.Start(port, IPAddress.Loopback);

        try
        {
            var hpA = new TaskCompletionSource<MobHpPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hpB = new TaskCompletionSource<MobHpPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var deathA = new TaskCompletionSource<MobDeathPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var deathB = new TaskCompletionSource<MobDeathPacket>(TaskCreationOptions.RunContinuationsAsynchronously);

            var clientA = ServerNet.CreateClient();
            clientA.OnReceived = data =>
            {
                ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
                if (packetId == MobHpPacket.Id)
                {
                    hpA.TrySetResult(serializer.Deserialize<MobHpPacket>(data.Span));
                }
                else if (packetId == MobDeathPacket.Id)
                {
                    deathA.TrySetResult(serializer.Deserialize<MobDeathPacket>(data.Span));
                }
                return ValueTask.CompletedTask;
            };

            var clientB = ServerNet.CreateClient();
            clientB.OnReceived = data =>
            {
                ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
                if (packetId == MobHpPacket.Id)
                {
                    hpB.TrySetResult(serializer.Deserialize<MobHpPacket>(data.Span));
                }
                else if (packetId == MobDeathPacket.Id)
                {
                    deathB.TrySetResult(serializer.Deserialize<MobDeathPacket>(data.Span));
                }
                return ValueTask.CompletedTask;
            };

            await Task.WhenAll(
                clientA.ConnectAsync("127.0.0.1", port),
                clientB.ConnectAsync("127.0.0.1", port));

            // 두 클라이언트 모두 같은 공유 보스의 MaxHp를 담은 HP 패킷을 받는다(첫 스텝은 스로틀
            // 없이 항상 즉시 브로드캐스트되므로 5초 안에 반드시 도착).
            var receivedHpA = await hpA.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var receivedHpB = await hpB.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.Equal(200, receivedHpA.MaxHp);
            Assert.Equal(200, receivedHpB.MaxHp);

            // 두 클라이언트 모두 같은 처치 통보(같은 MvpName·같은 Generation)를 받는다 — 공유 보스임을 실증.
            var receivedDeathA = await deathA.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var receivedDeathB = await deathB.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.StartsWith("player-", receivedDeathA.MvpName);
            Assert.Equal(receivedDeathA.MvpName, receivedDeathB.MvpName);
            Assert.Equal(receivedDeathA.Generation, receivedDeathB.Generation);
            Assert.True(receivedDeathA.TopDamage > 0);

            await clientA.DisposeAsync();
            await clientB.DisposeAsync();
        }
        finally
        {
            listener.Stop();
            lifetimeCts.Cancel();
        }
    }
}
