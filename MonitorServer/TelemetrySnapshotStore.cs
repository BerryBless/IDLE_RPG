namespace MonitorServer;

/// <summary>
/// <see cref="TelemetryClientLoop"/>(생산자, ServerLib I/O 스레드에서 갱신)가 쓰고, ASP.NET Core
/// 요청 파이프라인의 여러 스레드(소비자, <c>/events</c> SSE 핸들러)가 동시에 읽는 "최신 스냅샷 1개"
/// 홀더입니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="_current"/>를 참조 타입(불변
/// <see cref="MonitorSnapshot"/> record)으로 두고 <c>volatile</c>로 선언했다 — 참조 대입/읽기는
/// CLR에서 원래 원자적이고, <c>volatile</c>은 여기에 메모리 배리어를 추가해 한 스레드의 쓰기가
/// JIT/CPU 재정렬 없이 즉시 다른 스레드에 보이도록 강제한다. 레코드 자체가 불변이므로 한 번 읽은
/// 참조는 그 시점 스냅샷 전체를 일관되게 대표한다 — 필드 단위로 여러 개를 따로 갱신했다면 발생할
/// 수 있는 tearing(예: 새 BossCurrentHp + 이전 MvpName이 섞여 보이는 상황)이 원천적으로 없다.
/// 락(lock/Monitor)이 전혀 필요 없다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="Update"/>/<see cref="MarkDisconnected"/>
/// 호출마다 새 <see cref="MonitorSnapshot"/> 인스턴스가 할당된다(불변 record이므로 갱신 = 새 인스턴스
/// 교체). 1초 주기 저빈도 갱신이라 GC 영향은 무시할 수 있다.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 모든 멤버가 즉시 반환한다.</description></item>
/// </list>
/// </remarks>
public sealed class TelemetrySnapshotStore
{
    private volatile MonitorSnapshot _current = MonitorSnapshot.Disconnected;

    /// <summary>가장 최근에 갱신된 스냅샷입니다.</summary>
    public MonitorSnapshot Current => _current;

    /// <summary>GameServer로부터 새 <see cref="ServerLib.Core.Serialization.Packets.TelemetrySnapshotPacket"/>을 수신했을 때 호출한다.</summary>
    public void Update(MonitorSnapshot snapshot) => _current = snapshot;

    /// <summary>텔레메트리 연결이 끊겼을 때 호출한다. 마지막으로 관측된 수치는 유지한 채 <see cref="MonitorSnapshot.Connected"/>만 내린다.</summary>
    public void MarkDisconnected() => _current = _current with { Connected = false };
}
