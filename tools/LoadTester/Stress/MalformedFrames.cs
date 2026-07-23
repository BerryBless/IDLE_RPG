using System.Buffers.Binary;

namespace LoadTester.Stress;

/// <summary>
/// 서버 프레임 리더·인증 게이트의 강건성을 스트레스하기 위한 잘못된/악의적 바이트 프레임 빌더입니다.
/// 서버 프레이밍은 4바이트 헤더 <c>[ushort packetId LE][ushort bodyLen LE]</c> + 본문이며, 이 빌더들은
/// 그 규약을 의도적으로 위반하는 변종을 생성합니다. 전부 순수 함수라 단위 테스트로 바이트 레이아웃을
/// 완전히 검증합니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> Thread-safe(무상태 정적, 호출자가 Random을 소유). <b>[Memory:]</b> 호출당 byte[] 1개.
/// <b>[Blocking:]</b> Non-blocking.
/// </remarks>
public static class MalformedFrames
{
    /// <summary>서버 프레임 헤더 크기(바이트).</summary>
    public const int HeaderSize = 4;

    /// <summary>정상 인증 패킷 ID(악성 변종이 흉내내거나 비껴가는 기준값).</summary>
    public const ushort AuthTokenPacketId = 12;

    /// <summary>완전 무작위 바이트열(유효한 프레임 구조 자체가 없음).</summary>
    public static byte[] RandomGarbage(int length, Random rng)
    {
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length), length, "길이는 0 이상이어야 합니다.");
        var buffer = new byte[length];
        rng.NextBytes(buffer);
        return buffer;
    }

    /// <summary>본문 길이를 65535로 주장하지만 실제 본문은 몇 바이트만 보내는 프레임(리더가 나머지를 무한정 기다리게 유도).</summary>
    public static byte[] OversizedLengthHeader(int actualBodyBytes = 4)
    {
        if (actualBodyBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(actualBodyBytes), actualBodyBytes, "본문 바이트는 0 이상이어야 합니다.");
        var buffer = new byte[HeaderSize + actualBodyBytes];
        WriteHeader(buffer, AuthTokenPacketId, ushort.MaxValue); // 본문 65535 주장
        // 실제 본문은 actualBodyBytes만(전부 0) → 리더는 나머지 65531바이트를 계속 대기, 세션 유지.
        return buffer;
    }

    /// <summary>헤더가 주장하는 본문 길이보다 실제 본문을 적게 보내는 절단 프레임.</summary>
    public static byte[] TruncatedFrame(int claimedBodyLength, int actualBodyBytes)
    {
        if (claimedBodyLength is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(claimedBodyLength), claimedBodyLength, "주장 본문 길이는 0~65535여야 합니다.");
        if (actualBodyBytes < 0 || actualBodyBytes > claimedBodyLength)
            throw new ArgumentOutOfRangeException(nameof(actualBodyBytes), actualBodyBytes, "실제 본문은 0 이상 주장 길이 이하여야 합니다.");
        var buffer = new byte[HeaderSize + actualBodyBytes];
        WriteHeader(buffer, AuthTokenPacketId, claimedBodyLength);
        return buffer;
    }

    /// <summary>유효한 프레이밍이지만 인증 패킷이 아닌 ID(서버가 조용히 무시해야 함).</summary>
    public static byte[] WrongPacketId(ushort packetId, int bodyLength = 0)
    {
        if (packetId == AuthTokenPacketId)
            throw new ArgumentException("정상 인증 패킷 ID와 달라야 합니다.", nameof(packetId));
        if (bodyLength is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bodyLength), bodyLength, "본문 길이는 0~65535여야 합니다.");
        var buffer = new byte[HeaderSize + bodyLength];
        WriteHeader(buffer, packetId, bodyLength);
        return buffer;
    }

    /// <summary>유효한 인증 프레임 구조지만 본문이 무작위(디코드 시 예외 → 게이트가 catch → ack-false).</summary>
    public static byte[] ValidFrameGarbageBody(int bodyLength, Random rng)
    {
        if (bodyLength is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(bodyLength), bodyLength, "본문 길이는 1~65535여야 합니다.");
        var buffer = new byte[HeaderSize + bodyLength];
        WriteHeader(buffer, AuthTokenPacketId, bodyLength);
        rng.NextBytes(buffer.AsSpan(HeaderSize));
        // 본문 앞 2바이트(Token 길이 필드)가 남은 본문보다 크면 ReadString이 EndOfStream → 예외 → ack-false.
        return buffer;
    }

    /// <summary>본문 길이 0인 인증 프레임(Token 길이 필드조차 없어 디코드 실패 → ack-false).</summary>
    public static byte[] ZeroLengthBody()
    {
        var buffer = new byte[HeaderSize];
        WriteHeader(buffer, AuthTokenPacketId, 0);
        return buffer;
    }

    /// <summary>헤더 4바이트 미만(부분 헤더). 리더는 나머지를 대기, 세션 유지.</summary>
    public static byte[] PartialHeader()
    {
        // 2바이트만: packetId 절반만 도착한 상태를 흉내.
        return [0x0C, 0x00];
    }

    private static void WriteHeader(Span<byte> destination, ushort packetId, int bodyLength)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(destination, packetId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(2), (ushort)bodyLength);
    }
}
