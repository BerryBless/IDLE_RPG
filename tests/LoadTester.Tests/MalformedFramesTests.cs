using System.Buffers.Binary;
using LoadTester.Stress;

namespace LoadTester.Tests;

/// <summary><see cref="MalformedFrames"/> 빌더의 바이트 레이아웃 검증.</summary>
public class MalformedFramesTests
{
    private static (ushort Id, int BodyLen) Header(byte[] frame) =>
        (BinaryPrimitives.ReadUInt16LittleEndian(frame), BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(2)));

    [Fact]
    public void RandomGarbage_지정길이()
    {
        var frame = MalformedFrames.RandomGarbage(37, new Random(1));
        Assert.Equal(37, frame.Length);
    }

    [Fact]
    public void OversizedLengthHeader_본문65535주장_실제는4바이트()
    {
        var frame = MalformedFrames.OversizedLengthHeader();
        var (id, bodyLen) = Header(frame);
        Assert.Equal(12, id);
        Assert.Equal(65535, bodyLen);          // 주장
        Assert.Equal(4 + 4, frame.Length);     // 헤더4 + 실제본문4
    }

    [Fact]
    public void TruncatedFrame_주장보다_적은_본문()
    {
        var frame = MalformedFrames.TruncatedFrame(claimedBodyLength: 100, actualBodyBytes: 10);
        var (id, bodyLen) = Header(frame);
        Assert.Equal(12, id);
        Assert.Equal(100, bodyLen);
        Assert.Equal(4 + 10, frame.Length);
    }

    [Fact]
    public void TruncatedFrame_실제가_주장초과시_예외()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MalformedFrames.TruncatedFrame(10, 20));
    }

    [Fact]
    public void WrongPacketId_인증ID와_다름()
    {
        var frame = MalformedFrames.WrongPacketId(999, bodyLength: 8);
        var (id, bodyLen) = Header(frame);
        Assert.Equal(999, id);
        Assert.Equal(8, bodyLen);
        Assert.Equal(4 + 8, frame.Length);
    }

    [Fact]
    public void WrongPacketId_인증ID_지정시_예외()
    {
        Assert.Throws<ArgumentException>(() => MalformedFrames.WrongPacketId(12));
    }

    [Fact]
    public void ValidFrameGarbageBody_인증프레임_무작위본문()
    {
        var frame = MalformedFrames.ValidFrameGarbageBody(20, new Random(2));
        var (id, bodyLen) = Header(frame);
        Assert.Equal(12, id);
        Assert.Equal(20, bodyLen);
        Assert.Equal(4 + 20, frame.Length);
    }

    [Fact]
    public void ZeroLengthBody_헤더만()
    {
        var frame = MalformedFrames.ZeroLengthBody();
        var (id, bodyLen) = Header(frame);
        Assert.Equal(12, id);
        Assert.Equal(0, bodyLen);
        Assert.Equal(4, frame.Length);
    }

    [Fact]
    public void PartialHeader_4바이트미만()
    {
        var frame = MalformedFrames.PartialHeader();
        Assert.True(frame.Length < MalformedFrames.HeaderSize);
    }
}
