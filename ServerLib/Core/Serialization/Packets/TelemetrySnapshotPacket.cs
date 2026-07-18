using System.Text;
using ServerLib.Core.Serialization;

namespace ServerLib.Core.Serialization.Packets;

/// <summary>
/// GameServer의 텔레메트리 리스너(모니터링 전용, 읽기 전용 구독)가 접속한 모니터 프로세스 전원에게
/// 주기적으로 브로드캐스트하는 서버 상태 스냅샷 패킷입니다. 게임 클라이언트용 <see cref="MobHpPacket"/>/
/// <see cref="MobDeathPacket"/>과 달리 운영 모니터링 전용이며, 값은 전부 이미 스레드 안전한 집계
/// 신호(접속자 수·리스너 통계·레이드 액터의 onStep 콜백 값)에서만 채워집니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description>
/// <b>Thread Safety:</b> IPacket은 순수 데이터 홀더입니다. 다른 IPacket 구현체와 동일하게 인스턴스
/// 자체는 단일 스레드에서 직렬화/역직렬화해야 합니다.
/// </description></item>
/// <item><description>
/// <b>Memory Allocation:</b> class이므로 역직렬화 시 <c>new TelemetrySnapshotPacket()</c> 1회 +
/// <see cref="SpanReader.ReadString"/> 1회(<see cref="MvpName"/>) 힙 할당이 발생합니다. 1초 주기
/// 저빈도 브로드캐스트이므로 GC 영향은 무시할 수 있습니다(<see cref="MobDeathPacket"/>과 동일 근거).
/// </description></item>
/// <item><description>
/// <b>Blocking:</b> Non-blocking. 모든 직렬화/역직렬화 연산은 즉시 반환합니다.
/// </description></item>
/// <item><description>
/// <b>범위:</b> 플레이어별 상세(레벨/골드/기여도)는 포함하지 않습니다 — 해당 값은 소유 세션의 제출
/// 루프에서만 안전하게 읽을 수 있는 단일 소유자 상태라 텔레메트리 스레드에서 직접 읽으면 데이터
/// 레이스가 됩니다(<c>plan/web_monitoring_0718.md</c> 스코프 제외 근거 참고). 이 패킷의 모든 필드는
/// <c>ISessionRegistry.Count</c>/<c>IServerListener</c> 통계 또는 <c>RaidStepBroadcast</c> onStep
/// 콜백 값으로만 채워야 합니다.
/// </description></item>
/// </list>
/// </remarks>
// class 선택: 가변 길이 string(MvpName)을 담으므로 struct로 만들어도 string 참조 때문에 무할당이 불가
// (MobDeathPacket과 동일 근거). 1초 주기 저빈도 전송이라 클래스 힙 할당 1회는 GC 압력에 무시 가능.
public sealed class TelemetrySnapshotPacket : IPacket
{
    /// <summary>패킷 ID 상수입니다. 1~18 + 0xFFFE/0xFFFF가 이미 사용 중이라 다음 빈 값(19)을 배정합니다.</summary>
    public const ushort Id = 19;

    /// <inheritdoc/>
    public ushort PacketId => Id;

    /// <summary>현재 게임 리스너(7777)에 접속한 세션 수입니다. <c>IServerListener.ActiveSessionCount</c> 스냅샷.</summary>
    public int ConnectedCount { get; set; }

    /// <summary>게임 리스너의 accept 루프 구동 여부입니다. <c>IServerListener.IsRunning</c> 스냅샷.</summary>
    public bool IsRunning { get; set; }

    /// <summary>접속 상한 초과로 거부된 누적 연결 수입니다. <c>IServerListener.TotalRejectedConnections</c> 스냅샷.</summary>
    public long RejectedConnections { get; set; }

    /// <summary>공유 레이드 보스의 현재 HP입니다. 아직 관측된 스텝이 없으면 0입니다.</summary>
    public long BossCurrentHp { get; set; }

    /// <summary>공유 레이드 보스의 최대 HP입니다. 아직 관측된 스텝이 없으면 0입니다.</summary>
    public long BossMaxHp { get; set; }

    /// <summary>공유 레이드 보스의 현재 세대 번호입니다. 리스폰마다 1씩 증가합니다.</summary>
    public int Generation { get; set; }

    /// <summary>
    /// 마지막으로 관측된 레이드 스텝 이벤트 종류입니다. <c>GameServer.Systems.RaidEventType</c>을
    /// <c>byte</c>로 좁혀 담습니다(0=None, 1=BossDamaged, 2=BossDefeated, 3=RaidFailed) — ServerLib는
    /// GameServer 도메인 타입을 참조하지 않으므로(레이어 경계) enum 자체가 아닌 raw byte로 옮깁니다.
    /// </summary>
    public byte LastEvent { get; set; }

    /// <summary>마지막 보스 처치 시점 최대 기여자의 누적 피해량입니다. 처치 이벤트가 아니면 0입니다.</summary>
    public long TopDamage { get; set; }

    /// <summary>마지막 보스 처치 시점 최대 기여자(MVP)의 닉네임입니다. 처치 이벤트가 아니면 빈 문자열입니다.</summary>
    public string MvpName
    {
        get => _mvpName;
        set { _mvpName = value; _mvpNameBytes = -1; } // 캐시 무효화
    }

    private string _mvpName = string.Empty;

    // UTF-8 바이트 수 캐시(-1=미계산): GetBodySize↔Serialize 이중 스캔 방지 — setter에서 무효화(MobDeathPacket과 동일 패턴)
    private int _mvpNameBytes = -1;

    private int MvpNameByteCount => _mvpNameBytes >= 0
        ? _mvpNameBytes
        : (_mvpNameBytes = Encoding.UTF8.GetByteCount(_mvpName));

    /// <inheritdoc/>
    // 본문: int ConnectedCount(4B) + bool IsRunning(1B) + long RejectedConnections(8B)
    //     + long BossCurrentHp(8B) + long BossMaxHp(8B) + int Generation(4B) + byte LastEvent(1B)
    //     + long TopDamage(8B) + ushort len(2B) + UTF-8 MvpName = 44B 고정 + MvpName 가변
    public int GetBodySize() => 44 + MvpNameByteCount;

    /// <inheritdoc/>
    public void Serialize(ref SpanWriter writer)
    {
        writer.WriteInt32(ConnectedCount);
        writer.WriteBool(IsRunning);
        writer.WriteInt64(RejectedConnections);
        writer.WriteInt64(BossCurrentHp);
        writer.WriteInt64(BossMaxHp);
        writer.WriteInt32(Generation);
        writer.WriteByte(LastEvent);
        writer.WriteInt64(TopDamage);
        writer.WriteString(_mvpName, MvpNameByteCount);
    }

    /// <inheritdoc/>
    public void Deserialize(ref SpanReader reader)
    {
        ConnectedCount = reader.ReadInt32();
        IsRunning = reader.ReadBool();
        RejectedConnections = reader.ReadInt64();
        BossCurrentHp = reader.ReadInt64();
        BossMaxHp = reader.ReadInt64();
        Generation = reader.ReadInt32();
        LastEvent = reader.ReadByte();
        TopDamage = reader.ReadInt64();
        // ReadString = Alloc: 수신 버퍼(얕은 뷰)에서 새 string을 깊은복사로 생성 — string은 불변이라 zero-copy 불가.
        MvpName = reader.ReadString();
    }
}
