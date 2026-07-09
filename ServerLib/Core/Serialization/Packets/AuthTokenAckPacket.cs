namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// 게임 서버가 <see cref="AuthTokenPacket"/> 검증 결과를 클라이언트에 명시적으로 통지하는 응답 패킷입니다.
/// <see cref="LoginResponsePacket"/>과 대칭을 이루는 성공/실패 신호이며, 무응답 대신 이 패킷을 두어
/// 클라이언트가 인증 게이트 통과 여부를 간접 추론하지 않아도 되게 합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> IPacket은 순수 데이터 홀더입니다. 인스턴스를 여러
/// 스레드가 동시에 직렬화하지 마십시오.</description></item>
/// <item><description><b>Memory Allocation:</b> Zero-allocation. struct이므로 역직렬화 시
/// <c>new T()</c>가 스택/인라인 생성되고, bool 1개 외 참조 타입 필드가 없어 힙 할당이 없습니다.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 직렬화/역직렬화는 즉시 반환합니다.</description></item>
/// </list>
/// </remarks>
// struct 선택: bool 1개(값 타입)만 담아 힙 할당 유발 요인이 전혀 없음 — MobHpPacket과 동일 근거.
public struct AuthTokenAckPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다.</summary>
    public const ushort Id = 18;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>토큰 검증 성공 여부입니다.</summary>
    public bool Success { get; set; }

    /// <inheritdoc/>
    // 본문: bool Success (1B) 고정
    public int GetBodySize() => 1;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteBool(Success);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        Success = reader.ReadBool();
    }
}
