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
/// <c>Main.cs</c>의 전투 배선(연결 시 <see cref="SessionBattleRunner"/>가 자동 전투를 시작해
/// <see cref="MobHpPacket"/>/<see cref="MobDeathPacket"/>을 그 세션에 푸시)을 실제 루프백 TCP
/// 소켓으로 End-to-End 검증합니다.
/// </summary>
/// <remarks>
/// <c>Main.cs</c>는 top-level 문이라 테스트에서 직접 호출할 수 없습니다
/// (<c>SessionConnectionEndToEndTests</c>/<c>EchoEndToEndTests</c>와 동일한 이유). 대신
/// <see cref="SessionPlayerBinder"/> + <see cref="SessionBattleRunner"/>(둘 다 실제 프로덕션
/// 클래스)를 <see cref="ServerNet.CreateListener"/> 콜백에 Main.cs와 동일한 순서로 조합합니다.
/// <para>
/// <b>[결정론 확보]</b> 기본 레벨1 임시 플레이어의 데미지 판정에 의존하면, 고블린(Hp=55)을
/// 1틱에 죽이지 못하는 경우 몬스터의 반격으로 타이밍이 갈릴 수 있다. 그래서 무기(4001)의 공격력
/// 수정치를 100만으로 키운 커스텀 <see cref="EquipmentTable"/>을 주입해 매 틱 100% 즉사시킨다
/// (<see cref="GameServer.Systems.BattleLoop.Tick"/>은 몬스터가 죽으면 반격 분기 이전에 리턴하므로
/// 플레이어는 데미지를 받지 않는다) — 타이밍 경합 없이 결정론적으로 <see cref="MobDeathPacket"/>을
/// 받는다.
/// </para>
/// </remarks>
public class SessionBattleRunnerEndToEndTests
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

    /// <summary>무기 4001의 공격력을 100만으로 키운 커스텀 장비 테이블 — 매 틱 고블린(Hp=55)을 즉사시켜
    /// 몬스터의 반격이 결코 발생하지 않게 한다(결정론 확보).</summary>
    private static EquipmentTable BuildOneShotEquipmentTable() => new(new List<EquipmentTemplate>
    {
        new()
        {
            ItemMetaId = 4001, Name = "테스트용 대검", Slot = SlotType.Weapon, AttackScaling = 1.0f,
            BaseModifiers = [new StatModifier { StatType = StatType.Atk, ModType = ModifierType.FlatAdd, Value = 1_000_000 }]
        },
        new()
        {
            ItemMetaId = 5001, Name = "테스트용 갑옷", Slot = SlotType.Armor,
            BaseModifiers = [new StatModifier { StatType = StatType.Def, ModType = ModifierType.FlatAdd, Value = 0 }]
        },
        new()
        {
            ItemMetaId = 6001, Name = "테스트용 반지", Slot = SlotType.Accessory,
            BaseModifiers = [new StatModifier { StatType = StatType.CritProb, ModType = ModifierType.FlatAdd, Value = 0 }]
        }
    });

    [Fact]
    public async Task Connect_ReceivesMobHpPacket_ThenMobDeathPacket()
    {
        const int TimeoutMs = 5_000;

        var meterName = $"Test.SessionBattleRunner.{Guid.NewGuid()}";
        var metrics = new GameMetrics(meterName);
        await using var sink = new GameEventSink(new StringWriter(), metrics);

        var levelSystem = PlayerLevelSystem.CreateDefault();
        var binder = new SessionPlayerBinder(levelSystem, sink);
        var battleRunner = new SessionBattleRunner(
            levelSystem, MonsterTable.CreateDefault(), BuildOneShotEquipmentTable(), sink,
            tickInterval: TimeSpan.FromMilliseconds(5)); // 빠른 틱 — 테스트가 5초 안에 끝나야 함

        var serializer = new BinaryPacketSerializer();
        var hpReceived = new TaskCompletionSource<MobHpPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deathReceived = new TaskCompletionSource<MobDeathPacket>(TaskCreationOptions.RunContinuationsAsynchronously);

        int port = GetFreePort();
        IServerListener listener = ServerNet.CreateListener();

        // Main.cs와 동일한 순서로 조합: 연결 시 binder 먼저(Player를 Context에 부착) → battleRunner
        // (Player를 읽어 전투 루프 시작). 해제 시 반대 순서는 이 테스트에서 별도로 검증하지 않는다
        // (SessionConnectionEndToEndTests가 이미 그 부분을 검증함).
        listener.OnClientConnected = async session =>
        {
            await binder.OnConnected(session);
            await battleRunner.OnConnected(session);
        };
        listener.OnClientDisconnected = async session =>
        {
            await battleRunner.OnDisconnected(session);
            await binder.OnDisconnected(session);
        };
        listener.OnClientError = binder.OnError;

        listener.Start(port, IPAddress.Loopback);

        try
        {
            var client = ServerNet.CreateClient();

            // 헤더 앞 2바이트(PacketId, LittleEndian)로 분기 역직렬화 — client.OnReceived는 완전한
            // 패킷 1개(헤더+본문)를 그대로 받는다.
            client.OnReceived = data =>
            {
                ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
                if (packetId == MobHpPacket.Id)
                {
                    hpReceived.TrySetResult(serializer.Deserialize<MobHpPacket>(data.Span));
                }
                else if (packetId == MobDeathPacket.Id)
                {
                    deathReceived.TrySetResult(serializer.Deserialize<MobDeathPacket>(data.Span));
                }

                return ValueTask.CompletedTask;
            };

            await client.ConnectAsync("127.0.0.1", port);

            var hp = await hpReceived.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.True(hp.MaxHp > 0);
            Assert.True(hp.Generation >= 1);

            var death = await deathReceived.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.StartsWith("player-", death.MvpName);
            Assert.True(death.TopDamage > 0);
            Assert.True(death.Generation >= 1);

            await client.DisposeAsync();
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// "전투를 멀티로"의 핵심 주장 — 동시 접속한 각 세션이 서로 독립된 몬스터를 사냥한다는 것 —
    /// 을 실제로 검증한다. 위 테스트를 포함해 이제까지의 모든 통합 검증은 클라이언트 1개뿐이라
    /// <c>SessionBattleRunner</c> 내부 <c>_battles</c> 딕셔너리 키잉과 세션별 Player/Monster 격리가
    /// N=1에서만 확인된 상태였다. 여기서는 동시에 소켓 2개를 연결해, 각자 받은
    /// <see cref="MobDeathPacket.MvpName"/>이 서로 다른(교차 오염 없는) 자기 자신의
    /// <c>player.InstanceId</c>인지 확인한다.
    /// </summary>
    [Fact]
    public async Task TwoConcurrentConnections_EachReceivesOwnDistinctMvpName()
    {
        const int TimeoutMs = 5_000;

        var meterName = $"Test.SessionBattleRunner.{Guid.NewGuid()}";
        var metrics = new GameMetrics(meterName);
        await using var sink = new GameEventSink(new StringWriter(), metrics);

        var levelSystem = PlayerLevelSystem.CreateDefault();
        var binder = new SessionPlayerBinder(levelSystem, sink);
        var battleRunner = new SessionBattleRunner(
            levelSystem, MonsterTable.CreateDefault(), BuildOneShotEquipmentTable(), sink,
            tickInterval: TimeSpan.FromMilliseconds(5));

        var serializer = new BinaryPacketSerializer();

        int port = GetFreePort();
        IServerListener listener = ServerNet.CreateListener();

        listener.OnClientConnected = async session =>
        {
            await binder.OnConnected(session);
            await battleRunner.OnConnected(session);
        };
        listener.OnClientDisconnected = async session =>
        {
            await battleRunner.OnDisconnected(session);
            await binder.OnDisconnected(session);
        };
        listener.OnClientError = binder.OnError;

        listener.Start(port, IPAddress.Loopback);

        try
        {
            // 두 클라이언트를 동시에 연결한다 — 서로 다른 SessionId로 SessionBattleRunner._battles에
            // 각각 등록되어야 격리가 성립한다(같은 딕셔너리, 다른 키).
            var deathA = new TaskCompletionSource<MobDeathPacket>(TaskCreationOptions.RunContinuationsAsynchronously);
            var deathB = new TaskCompletionSource<MobDeathPacket>(TaskCreationOptions.RunContinuationsAsynchronously);

            var clientA = ServerNet.CreateClient();
            clientA.OnReceived = data =>
            {
                ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
                if (packetId == MobDeathPacket.Id)
                {
                    deathA.TrySetResult(serializer.Deserialize<MobDeathPacket>(data.Span));
                }
                return ValueTask.CompletedTask;
            };

            var clientB = ServerNet.CreateClient();
            clientB.OnReceived = data =>
            {
                ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
                if (packetId == MobDeathPacket.Id)
                {
                    deathB.TrySetResult(serializer.Deserialize<MobDeathPacket>(data.Span));
                }
                return ValueTask.CompletedTask;
            };

            await Task.WhenAll(
                clientA.ConnectAsync("127.0.0.1", port),
                clientB.ConnectAsync("127.0.0.1", port));

            var resultA = await deathA.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            var resultB = await deathB.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            Assert.StartsWith("player-", resultA.MvpName);
            Assert.StartsWith("player-", resultB.MvpName);
            Assert.NotEqual(resultA.MvpName, resultB.MvpName); // 세션별 독립 Player — 교차 오염 없음

            await clientA.DisposeAsync();
            await clientB.DisposeAsync();
        }
        finally
        {
            listener.Stop();
        }
    }
}
