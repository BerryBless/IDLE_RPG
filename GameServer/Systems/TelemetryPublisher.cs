using System.Buffers;
using System.Threading.Channels;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace GameServer.Systems;

/// <summary>
/// <see cref="RaidEncounter"/> 액터 루프의 <c>onStep</c> 콜백을 <see cref="RaidBroadcaster"/>와 나란히
/// 구독하는 두 번째 컨슈머로서, 보스의 최신 스텝 값을 래치해두었다가 1초 주기로 접속자 수·리스너
/// 통계와 합쳐 <see cref="TelemetrySnapshotPacket"/>을 텔레메트리 구독자(모니터링 전용 별도 프로세스)
/// 전원에게 브로드캐스트한다. 게임 클라이언트용 <see cref="RaidBroadcaster"/>와 완전히 독립된 별도
/// 전송 경로다 — 텔레메트리 구독자가 아무리 느리거나 끊겨도 게임 클라이언트 브로드캐스트나 레이드
/// 액터 자체에는 전혀 영향을 주지 않는다(아래 remarks 참고).
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>onStep은 절대 액터를 지연시키지 않는다(RaidBroadcaster.OnStepAsync와 동일
/// 계약):</b> <see cref="OnStep"/>은 <see cref="_bossLatestChannel"/>에 <c>TryWrite</c>만 하고 즉시
/// 반환한다. 실제 네트워크 브로드캐스트는 <see cref="PublishLoopAsync"/>가 자체 <see cref="PeriodicTimer"/>
/// 주기로 전담한다.</description></item>
/// <item><description><b>"최신 값만 유지" 락-프리 메일박스:</b> <see cref="_bossLatestChannel"/>은
/// 용량 1의 <see cref="Channel{T}"/>를 <see cref="BoundedChannelFullMode.DropOldest"/>로 구성한다 —
/// <see cref="RaidStepBroadcast"/>는 값 타입(여러 필드)이라 필드별로 따로 쓰면 서로 다른 스텝의 값이
/// 섞이는 tearing이 생길 수 있는데, Channel의 단일 슬롯 교체는 슬롯 전체를 원자적으로 바꿔치기하므로
/// 락 없이도 "직전 스텝 통째로 보이거나 최신 스텝 통째로 보이거나" 둘 중 하나만 관측된다. 액터
/// (SingleWriter)가 아무리 빨리 스텝을 밀어넣어도, 아직 소비되지 않은 이전 값은 자동으로 버려지고
/// 최신 값으로 교체될 뿐 큐가 무한 증가하지 않는다.</description></item>
/// <item><description><b>Thread Safety:</b> <see cref="OnStep"/>은 액터의 단일 스레드에서만 순차
/// 호출된다는 전제(<c>SingleWriter</c>)이고, <see cref="PublishLoopAsync"/>는 인스턴스당 정확히 1개만
/// 실행돼야 한다는 전제(<c>SingleReader</c>) — 둘 다 호출자(<see cref="SessionRaidRunner"/>)가
/// 보장한다.</description></item>
/// <item><description><b>범위:</b> 이 클래스가 조립하는 <see cref="TelemetrySnapshotPacket"/>은
/// 이미 스레드 안전한 신호(<see cref="IServerListener.ActiveSessionCount"/>/<see cref="IServerListener.IsRunning"/>/
/// <see cref="IServerListener.TotalRejectedConnections"/> 및 <see cref="RaidStepBroadcast"/> 값)만
/// 담는다. 플레이어별 상세(레벨/골드/기여도)는 절대 포함하지 않는다 — 그 상태는 소유 세션의 제출
/// 루프에서만 안전하게 읽을 수 있는 단일 소유자 데이터이기 때문이다(<c>plan/web_monitoring_0718.md</c>
/// 스코프 제외 근거 참고).</description></item>
/// </list>
/// </remarks>
public sealed class TelemetryPublisher
{
    private static readonly TimeSpan DefaultPublishInterval = TimeSpan.FromSeconds(1);

    private readonly IServerListener _gameListener;
    private readonly ISessionRegistry _telemetryRegistry;
    private readonly TimeSpan _publishInterval;

    // BinaryPacketSerializer: 무상태(내부 가변 필드 없음)라 퍼블리시 루프가 매 주기 재사용해도
    // 안전하다(RaidBroadcaster._serializer와 동일 근거).
    private readonly BinaryPacketSerializer _serializer = new();

    // Channel<RaidStepBroadcast> 용량 1 + DropOldest: "최신 스텝 1개만 유지"하는 락-프리 단일 슬롯
    // 메일박스. SingleWriter=true(OnStep은 액터의 단일 스레드에서만 순차 호출), SingleReader=true
    // (퍼블리시 루프 1개) — 위 클래스 remarks의 tearing 방지 근거 참고.
    private readonly Channel<RaidStepBroadcast> _bossLatestChannel = Channel.CreateBounded<RaidStepBroadcast>(
        new BoundedChannelOptions(1)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });

    // PublishLoopAsync(단일 소비자) 전용 캐시 — 이 필드의 유일한 리더/라이터는 그 루프이므로 락 불필요.
    // 서버 기동 직후 아직 레이드 스텝이 한 번도 없었던 구간에는 default(RaidStepBroadcast)(모든 필드 0,
    // MvpName=null)를 그대로 사용하고, 패킷 조립 시 null을 빈 문자열로 방어한다.
    private RaidStepBroadcast _cachedBossStep;

    /// <summary>텔레메트리 퍼블리셔를 생성한다.</summary>
    /// <param name="gameListener">접속자 수·가동 상태·거부된 연결 수를 읽어올 게임 리스너(7777)</param>
    /// <param name="telemetryRegistry">브로드캐스트 대상(접속한 모니터 프로세스 전체)을 추적하는 전용 레지스트리</param>
    /// <param name="publishInterval">스냅샷 전송 주기. 생략 시 1초 — 운영 대시보드의 신선도 하한이다.</param>
    public TelemetryPublisher(IServerListener gameListener, ISessionRegistry telemetryRegistry, TimeSpan? publishInterval = null)
    {
        ArgumentNullException.ThrowIfNull(gameListener);
        ArgumentNullException.ThrowIfNull(telemetryRegistry);
        _gameListener = gameListener;
        _telemetryRegistry = telemetryRegistry;
        _publishInterval = publishInterval ?? DefaultPublishInterval;
    }

    /// <summary>
    /// <see cref="RaidEncounter.RunAsync"/>의 onStep 콜백으로 <see cref="RaidBroadcaster.OnStepAsync"/>와
    /// 나란히 전달할 델리게이트. <see cref="_bossLatestChannel"/>에 <c>TryWrite</c>만 하고 즉시 반환한다.
    /// </summary>
    /// <remarks>
    /// <b>Blocking 여부:</b> Non-blocking, 동기 반환(async 상태 머신조차 없음). 용량 1 채널의
    /// <c>TryWrite</c>는 <c>DropOldest</c> 모드라 가득 차 있어도 항상 즉시 성공한다(오래된 값을
    /// 버리고 자리를 만든 뒤 씀).
    /// </remarks>
    public ValueTask OnStep(RaidStepBroadcast info, CancellationToken cancellationToken)
    {
        _bossLatestChannel.Writer.TryWrite(info);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// <see cref="_bossLatestChannel"/>에서 최신 보스 스텝을 흡수하고, 게임 리스너 통계와 합쳐
    /// <see cref="TelemetrySnapshotPacket"/>을 1초 주기로 텔레메트리 구독자 전원에게 브로드캐스트하는
    /// 단일 퍼블리시 루프. 호출자가 인스턴스당 정확히 1개만 실행해야 한다(<c>SingleReader</c>).
    /// </summary>
    /// <param name="lifetimeToken">서버 종료 시 취소되는 수명 토큰</param>
    /// <remarks>
    /// <b>Blocking 여부:</b> Non-blocking. <see cref="PeriodicTimer.WaitForNextTickAsync(CancellationToken)"/>와
    /// 실제 브로드캐스트 <c>await</c>에서만 대기하며 호출 스레드를 점유하지 않는다.
    /// <b>Thread Safety:</b> 이 태스크가 <see cref="_cachedBossStep"/>의 유일한 리더/라이터다(단일
    /// 소비자, 락 불필요).
    /// </remarks>
    public async Task PublishLoopAsync(CancellationToken lifetimeToken)
    {
        // PeriodicTimer: 단일 OS 타이머 핸들을 재사용해 WaitForNextTickAsync를 반복 호출해도 매번 새
        // 타이머를 등록/해제하지 않는다(SessionRaidRunner.SubmitLoopAsync와 동일 근거로 Task.Delay 대비 절감).
        using var timer = new PeriodicTimer(_publishInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(lifetimeToken))
            {
                // 이번 주기 동안 쌓인 스텝 중 가장 최신 것만 남을 때까지 흡수(용량 1 채널이라 최대 1개).
                while (_bossLatestChannel.Reader.TryRead(out var step))
                {
                    _cachedBossStep = step;
                }

                var packet = new TelemetrySnapshotPacket
                {
                    ConnectedCount = _gameListener.ActiveSessionCount,
                    IsRunning = _gameListener.IsRunning,
                    RejectedConnections = _gameListener.TotalRejectedConnections,
                    BossCurrentHp = _cachedBossStep.CurrentHp,
                    BossMaxHp = _cachedBossStep.MaxHp,
                    Generation = _cachedBossStep.NewGeneration,
                    LastEvent = (byte)_cachedBossStep.Event,
                    TopDamage = _cachedBossStep.TopDamage,
                    MvpName = _cachedBossStep.MvpName ?? string.Empty, // default(RaidStepBroadcast).MvpName은 null(구조체 기본값) — 문자열 필드 방어
                };

                await BroadcastPacketAsync(packet, lifetimeToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료(서버 수명 토큰 취소).
        }
    }

    /// <summary>패킷 1개를 직렬화해 접속한 모든 텔레메트리 구독자에게 브로드캐스트한다.</summary>
    /// <remarks>
    /// <b>Memory Allocation:</b> <see cref="ArrayPool{T}"/>.Shared에서 버퍼를 대여해 직렬화하고,
    /// <see cref="ISessionRegistry.BroadcastAsync"/>가 요구하는 대로 그 <c>ValueTask</c>가 완료될
    /// 때까지(모든 구독자 전송 완료) 버퍼를 유효하게 유지한 뒤 <c>finally</c>에서 반납한다
    /// (<see cref="RaidBroadcaster.BroadcastPacketAsync{T}"/>와 동일 패턴).
    /// </remarks>
    private async ValueTask BroadcastPacketAsync<T>(T packet, CancellationToken cancellationToken) where T : IPacket
    {
        // ArrayPool<byte>.Shared: 고정 크기 버킷 풀로 TLS 슬롯을 우선 확인하므로, 1초 주기 퍼블리시
        // 루프가 매번 대여·반납해도 힙 할당 없이 O(1)로 재사용된다.
        var buffer = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize + packet.GetBodySize());
        try
        {
            int written = _serializer.Serialize(packet, buffer);
            await _telemetryRegistry.BroadcastAsync(buffer.AsMemory(0, written), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
