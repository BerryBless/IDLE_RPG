namespace MonitorServer;

/// <summary>
/// 웹 대시보드(<c>/events</c> SSE)로 JSON 직렬화해 내보내는 불변 스냅샷입니다. GameServer가 보내는
/// <see cref="ServerLib.Core.Serialization.Packets.TelemetrySnapshotPacket"/>의 필드를 그대로
/// 옮기되, 이 텔레메트리 연결 자체의 생사(<see cref="Connected"/>)와 마지막 갱신 시각을 추가로
/// 담아 "GameServer가 아예 꺼져 있음"과 "접속자가 0명인 정상 상태"를 브라우저에서 구분할 수 있게
/// 합니다.
/// </summary>
/// <param name="Connected">GameServer 텔레메트리 리스너(7779)에 현재 연결돼 있는지 여부입니다.</param>
/// <param name="UpdatedAtUtc">이 스냅샷이 갱신된 UTC 시각입니다. <see cref="Connected"/>가
/// <see langword="false"/>이면 마지막으로 연결이 끊긴 시각을 의미합니다.</param>
/// <param name="ConnectedCount">게임 리스너(7777)에 접속한 세션 수입니다.</param>
/// <param name="IsRunning">게임 리스너의 accept 루프 구동 여부입니다.</param>
/// <param name="RejectedConnections">접속 상한 초과로 거부된 누적 연결 수입니다.</param>
/// <param name="BossCurrentHp">공유 레이드 보스의 현재 HP입니다.</param>
/// <param name="BossMaxHp">공유 레이드 보스의 최대 HP입니다.</param>
/// <param name="Generation">공유 레이드 보스의 현재 세대 번호입니다.</param>
/// <param name="LastEvent">마지막으로 관측된 레이드 스텝 이벤트(0=None,1=BossDamaged,2=BossDefeated,3=RaidFailed)입니다.</param>
/// <param name="TopDamage">마지막 처치 시점 최대 기여자의 누적 피해량입니다.</param>
/// <param name="MvpName">마지막 처치 시점 최대 기여자(MVP)의 닉네임입니다.</param>
/// <remarks>
/// <b>Thread Safety:</b> 불변(record)이므로 여러 스레드가 동시에 같은 인스턴스를 읽어도 안전합니다.
/// <b>Memory Allocation:</b> System.Text.Json이 리플렉션 기반 직렬화기로 이 record를 매 SSE 틱마다
/// 직렬화합니다 — 저빈도(1초 주기, 접속한 브라우저 수만큼)라 GC 영향은 무시할 수 있습니다.
/// </remarks>
public sealed record MonitorSnapshot(
    bool Connected,
    DateTime UpdatedAtUtc,
    int ConnectedCount,
    bool IsRunning,
    long RejectedConnections,
    long BossCurrentHp,
    long BossMaxHp,
    int Generation,
    byte LastEvent,
    long TopDamage,
    string MvpName)
{
    /// <summary>GameServer 텔레메트리 리스너에 아직 한 번도 연결하지 못했을 때의 초기값입니다.</summary>
    public static readonly MonitorSnapshot Disconnected = new(
        Connected: false,
        UpdatedAtUtc: default,
        ConnectedCount: 0,
        IsRunning: false,
        RejectedConnections: 0,
        BossCurrentHp: 0,
        BossMaxHp: 0,
        Generation: 0,
        LastEvent: 0,
        TopDamage: 0,
        MvpName: string.Empty);
}
