using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;

namespace AuthServer.Tests;

/// <summary>
/// <see cref="LoginRequestPacket"/>/<see cref="LoginResponsePacket"/>/<see cref="AuthTokenPacket"/>의
/// 직렬화·역직렬화 계약을 소켓 없이 단위 검증한다. 이 세 패킷은 이번 사이클 이전부터 <c>ServerLib</c>에
/// 정의만 돼 있었을 뿐 왕복 테스트가 없었으므로, 배선(<see cref="AuthServer.Login.AuthConnectionHandler"/>)이
/// 의존하기 전에 기존 구현의 정확성을 특성화(characterize)한다.
/// </summary>
public class LoginPacketRoundTripTests
{
    private static readonly BinaryPacketSerializer Serializer = new();

    private static byte[] AllocateBuffer(IPacket packet)
        => new byte[PacketPool.HeaderSize + packet.GetBodySize()];

    // ── LoginRequestPacket ───────────────────────────────────────────────────

    [Theory]
    [InlineData("", "")]
    [InlineData("alice", "hunter2")]
    [InlineData("한글사용자", "한글비밀번호")]
    [InlineData("🎯user", "🎯pass")]
    public void LoginRequestPacket_RoundTrip_PreservesUsernameAndPassword(string username, string password)
    {
        var packet = new LoginRequestPacket { Username = username, Password = password };
        var buf = AllocateBuffer(packet);

        Serializer.Serialize(packet, buf);
        var result = Serializer.Deserialize<LoginRequestPacket>(buf);

        Assert.Equal(username, result.Username);
        Assert.Equal(password, result.Password);
    }

    [Fact]
    public void LoginRequestPacket_PacketId_IsTen()
    {
        Assert.Equal((ushort)10, LoginRequestPacket.Id);
        Assert.Equal((ushort)10, new LoginRequestPacket().PacketId);
    }

    // ── LoginResponsePacket ──────────────────────────────────────────────────

    [Theory]
    [InlineData(true, "some.token.value")]
    [InlineData(false, "")]
    [InlineData(true, "")]
    public void LoginResponsePacket_RoundTrip_PreservesSuccessAndToken(bool success, string token)
    {
        var packet = new LoginResponsePacket { Success = success, Token = token };
        var buf = AllocateBuffer(packet);

        Serializer.Serialize(packet, buf);
        var result = Serializer.Deserialize<LoginResponsePacket>(buf);

        Assert.Equal(success, result.Success);
        Assert.Equal(token, result.Token);
    }

    [Fact]
    public void LoginResponsePacket_PacketId_IsEleven()
    {
        Assert.Equal((ushort)11, LoginResponsePacket.Id);
        Assert.Equal((ushort)11, new LoginResponsePacket().PacketId);
    }

    // ── AuthTokenPacket ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("abc123.def456")]
    [InlineData("🎯tokenwithemoji")]
    public void AuthTokenPacket_RoundTrip_PreservesToken(string token)
    {
        var packet = new AuthTokenPacket { Token = token };
        var buf = AllocateBuffer(packet);

        Serializer.Serialize(packet, buf);
        var result = Serializer.Deserialize<AuthTokenPacket>(buf);

        Assert.Equal(token, result.Token);
    }

    [Fact]
    public void AuthTokenPacket_PacketId_IsTwelve()
    {
        Assert.Equal((ushort)12, AuthTokenPacket.Id);
        Assert.Equal((ushort)12, new AuthTokenPacket().PacketId);
    }
}
