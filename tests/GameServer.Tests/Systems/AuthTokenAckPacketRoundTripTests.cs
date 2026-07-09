using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;

namespace GameServer.Tests.Systems;

/// <summary>
/// <see cref="AuthTokenAckPacket"/>의 직렬화·역직렬화 계약을 소켓 없이 단위 검증한다.
/// GameServer가 <see cref="AuthTokenPacket"/> 검증 결과를 클라이언트에 명시적으로 통지하는
/// 응답 패킷이며, GameServer가 유일한 발신 주체이므로 이 패킷의 왕복 테스트는 여기(GameServer.Tests)에 둔다.
/// </summary>
public class AuthTokenAckPacketRoundTripTests
{
    private static readonly BinaryPacketSerializer Serializer = new();

    private static byte[] AllocateBuffer(IPacket packet)
        => new byte[PacketPool.HeaderSize + packet.GetBodySize()];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AuthTokenAckPacket_RoundTrip_PreservesSuccess(bool success)
    {
        var packet = new AuthTokenAckPacket { Success = success };
        var buf = AllocateBuffer(packet);

        Serializer.Serialize(packet, buf);
        var result = Serializer.Deserialize<AuthTokenAckPacket>(buf);

        Assert.Equal(success, result.Success);
    }

    [Fact]
    public void AuthTokenAckPacket_GetBodySize_IsOneByte()
    {
        var packet = new AuthTokenAckPacket { Success = true };

        Assert.Equal(1, packet.GetBodySize());
    }

    [Fact]
    public void AuthTokenAckPacket_PacketId_IsEighteen()
    {
        Assert.Equal((ushort)18, AuthTokenAckPacket.Id);
        Assert.Equal((ushort)18, new AuthTokenAckPacket().PacketId);
    }
}
