using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;

namespace GameServer.Tests.Systems;

/// <summary>
/// <see cref="TelemetrySnapshotPacket"/>의 직렬화·역직렬화 계약을 소켓 없이 단위 검증한다.
/// GameServer의 텔레메트리 리스너가 유일한 발신 주체이므로 이 패킷의 왕복 테스트는 여기
/// (GameServer.Tests)에 둔다(<see cref="AuthTokenAckPacketRoundTripTests"/>와 동일 배치 근거).
/// </summary>
public class TelemetrySnapshotPacketRoundTripTests
{
    private static readonly BinaryPacketSerializer Serializer = new();

    private static byte[] AllocateBuffer(IPacket packet)
        => new byte[PacketPool.HeaderSize + packet.GetBodySize()];

    [Fact]
    public void TelemetrySnapshotPacket_RoundTrip_PreservesAllFields()
    {
        var packet = new TelemetrySnapshotPacket
        {
            ConnectedCount = 42,
            IsRunning = true,
            RejectedConnections = 7,
            BossCurrentHp = 123_456,
            BossMaxHp = 5_000_000,
            Generation = 3,
            LastEvent = 2, // RaidEventType.BossDefeated
            TopDamage = 987_654,
            MvpName = "테스트유저",
        };
        var buf = AllocateBuffer(packet);

        Serializer.Serialize(packet, buf);
        var result = Serializer.Deserialize<TelemetrySnapshotPacket>(buf);

        Assert.Equal(packet.ConnectedCount, result.ConnectedCount);
        Assert.Equal(packet.IsRunning, result.IsRunning);
        Assert.Equal(packet.RejectedConnections, result.RejectedConnections);
        Assert.Equal(packet.BossCurrentHp, result.BossCurrentHp);
        Assert.Equal(packet.BossMaxHp, result.BossMaxHp);
        Assert.Equal(packet.Generation, result.Generation);
        Assert.Equal(packet.LastEvent, result.LastEvent);
        Assert.Equal(packet.TopDamage, result.TopDamage);
        Assert.Equal(packet.MvpName, result.MvpName);
    }

    [Fact]
    public void TelemetrySnapshotPacket_RoundTrip_EmptyMvpNameDefaultsCorrectly()
    {
        var packet = new TelemetrySnapshotPacket
        {
            ConnectedCount = 0,
            IsRunning = false,
            RejectedConnections = 0,
            BossCurrentHp = 0,
            BossMaxHp = 0,
            Generation = 0,
            LastEvent = 0, // RaidEventType.None
            TopDamage = 0,
        };
        var buf = AllocateBuffer(packet);

        Serializer.Serialize(packet, buf);
        var result = Serializer.Deserialize<TelemetrySnapshotPacket>(buf);

        Assert.Equal(string.Empty, result.MvpName);
        Assert.False(result.IsRunning);
    }

    [Fact]
    public void TelemetrySnapshotPacket_GetBodySize_MatchesFixedPlusMvpNameLength()
    {
        var packet = new TelemetrySnapshotPacket { MvpName = "abc" };

        Assert.Equal(44 + 3, packet.GetBodySize());
    }

    [Fact]
    public void TelemetrySnapshotPacket_PacketId_IsNineteen()
    {
        Assert.Equal((ushort)19, TelemetrySnapshotPacket.Id);
        Assert.Equal((ushort)19, new TelemetrySnapshotPacket().PacketId);
    }
}
