using System.Buffers;
using System.Threading.Channels;
using ServerLib.Core;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Interface;

namespace GameServer.Systems;

/// <summary>
/// <see cref="RaidEncounter"/> 액터 루프의 <c>onStep</c> 콜백을 받아 접속한 모든 세션에 보스 HP/처치
/// 브로드캐스트를 전송하는 책임만 전담한다. <see cref="SessionRaidRunner"/>의 SRP 위반(코드리뷰
/// Medium 발견, <c>docs/code-reviews/2026-07-08-shared-boss-raid-coop-review.md</c>) 수정으로
/// 분리됐다 — 세션 생명주기·보상 적용과 무관하게 "직렬화 + 스로틀 + 네트워크 전송"만 안다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>액터와 완전히 분리된 전송(코드리뷰 HIGH 발견 수정 계승):</b> <see cref="OnStepAsync"/>는
/// <see cref="_broadcastChannel"/>에 <c>TryWrite</c>만 하고 즉시 반환한다. 실제
/// <see cref="ISessionRegistry.BroadcastAsync"/> 네트워크 전송은 <see cref="DrainAsync"/> 단일
/// 소비자 태스크가 전담해, 느리거나 영원히 끝나지 않는 전송이 있어도 이 인스턴스를 <c>onStep</c>으로
/// 호출하는 액터 루프는 전혀 지연되지 않는다.</description></item>
/// <item><description><b>Thread Safety:</b> <see cref="OnStepAsync"/>는 액터의 단일 스레드에서만
/// 순차 호출된다는 전제(<c>SingleWriter</c>)이고, <see cref="DrainAsync"/>는 이 인스턴스당 정확히
/// 1개만 실행돼야 한다는 전제(<c>SingleReader</c>) — 둘 다 호출자(<see cref="SessionRaidRunner"/>)가
/// 보장한다.</description></item>
/// </list>
/// </remarks>
public sealed class RaidBroadcaster
{
    // HP 브로드캐스트 스로틀: BossDamaged/None 스텝은 이 간격보다 자주 전 세션에 보내지 않는다(N명
    // 접속 시 매 피해 제출마다 브로드캐스트하면 틱당 N×N 전송이 되는 것을 방지). 처치/실패는 항상
    // 즉시 보낸다(DrainAsync에서 이벤트 종류로 분기).
    private static readonly TimeSpan DefaultHpBroadcastThrottle = TimeSpan.FromMilliseconds(150);

    private readonly ISessionRegistry _registry;
    private readonly TimeSpan _hpBroadcastThrottle;

    // BinaryPacketSerializer: 무상태(내부 가변 필드 없음)라 드레인 태스크가 반복 호출해도 안전 —
    // PacketSendExtensions와 동일하게 인스턴스 1개를 공유한다(직렬화기 재할당 방지).
    private readonly BinaryPacketSerializer _serializer = new();

    // Channel<RaidStepBroadcast>: 액터(생산자, OnStepAsync를 통해)→드레인 태스크(단일 소비자) 큐.
    // SingleWriter=true(onStep은 액터의 단일 스레드에서만 순차 호출), SingleReader=true(드레인
    // 태스크 1개). Unbounded — 원소가 작은 값 타입 struct라 소비가 밀려도 생산자(액터)의 가용성에는
    // 영향이 없다(생산자는 이 채널에 절대 TryRead/await하지 않음).
    private readonly Channel<RaidStepBroadcast> _broadcastChannel = Channel.CreateUnbounded<RaidStepBroadcast>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

    // DateTime(DrainAsync 전용 필드): 단일 소비자이므로 이 필드의 유일한 리더/라이터다(락 불필요).
    private DateTime _lastHpBroadcastUtc = DateTime.MinValue;

    /// <summary>레이드 브로드캐스터를 생성한다.</summary>
    /// <param name="registry">브로드캐스트 대상(접속 세션 전체)을 추적하는 레지스트리</param>
    /// <param name="hpBroadcastThrottle">HP 전용 스텝(BossDamaged/None)의 최소 전송 간격. 생략 시 150ms.</param>
    public RaidBroadcaster(ISessionRegistry registry, TimeSpan? hpBroadcastThrottle = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _hpBroadcastThrottle = hpBroadcastThrottle ?? DefaultHpBroadcastThrottle;
    }

    /// <summary>
    /// <see cref="RaidEncounter.RunAsync"/>의 onStep 콜백으로 전달할 델리게이트. <see cref="_broadcastChannel"/>에
    /// <c>TryWrite</c>만 하고 즉시 반환한다 — 실제 네트워크 전송·스로틀 판정은 전혀 하지 않는다.
    /// </summary>
    /// <remarks>
    /// <b>Blocking 여부:</b> Non-blocking, 동기 반환(async 상태 머신조차 없음). 무경계 채널의
    /// <c>TryWrite</c>는 항상 즉시 성공한다.
    /// </remarks>
    public ValueTask OnStepAsync(RaidStepBroadcast info, CancellationToken cancellationToken)
    {
        _broadcastChannel.Writer.TryWrite(info);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// <see cref="_broadcastChannel"/>을 소비하며 실제 네트워크 브로드캐스트와 HP 스로틀 판정을
    /// 전담하는 단일 드레인 태스크. 호출자가 인스턴스당 정확히 1개만 실행해야 한다(<c>SingleReader</c>).
    /// </summary>
    /// <remarks>
    /// <b>Blocking 여부:</b> Non-blocking. <c>ReadAllAsync</c>와 실제 브로드캐스트 <c>await</c>에서만
    /// 대기하며 호출 스레드를 점유하지 않는다. <b>Thread Safety:</b> 이 태스크가
    /// <see cref="_lastHpBroadcastUtc"/>의 유일한 리더/라이터다(단일 소비자, 락 불필요).
    /// <b>코드리뷰 Medium 발견 수정:</b> 스로틀 타임스탬프를 브로드캐스트 <c>await</c> 완료
    /// <b>이후</b>에 찍는다 — 이전에는 await 이전에 찍어, 브로드캐스트 1회가
    /// <see cref="_hpBroadcastThrottle"/>를 넘으면 다음 스텝이 항상 "이미 경과함"으로 판정돼 스로틀이
    /// 사실상 무력화됐다(보호가 가장 필요한 느린 구간에서 정확히 보호가 사라지는 버그).
    /// </remarks>
    public async Task DrainAsync(CancellationToken lifetimeToken)
    {
        try
        {
            await foreach (var info in _broadcastChannel.Reader.ReadAllAsync(lifetimeToken))
            {
                bool isThrottledHpOnly = info.Event is RaidEventType.None or RaidEventType.BossDamaged;
                if (isThrottledHpOnly && DateTime.UtcNow - _lastHpBroadcastUtc < _hpBroadcastThrottle)
                {
                    continue; // 스로틀 — 이번 HP 브로드캐스트는 건너뛴다(보스 판정 자체는 이미 끝난 뒤라 영향 없음)
                }

                var (death, hp) = RaidBroadcastPackets.Build(info);
                if (death is not null)
                {
                    await BroadcastPacketAsync(death, lifetimeToken);
                }
                await BroadcastPacketAsync(hp, lifetimeToken);

                if (isThrottledHpOnly)
                {
                    _lastHpBroadcastUtc = DateTime.UtcNow; // await 완료 후 갱신 — 위 remarks 참고
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료(서버 수명 토큰 취소).
        }
    }

    /// <summary>패킷 1개를 직렬화해 접속한 모든 세션에 브로드캐스트한다.</summary>
    /// <remarks>
    /// <b>Memory Allocation:</b> <see cref="ArrayPool{T}"/>.Shared에서 버퍼를 대여해 직렬화하고,
    /// <see cref="ISessionRegistry.BroadcastAsync"/>가 요구하는 대로 그 <c>ValueTask</c>가 완료될
    /// 때까지(모든 세션 전송 완료) 버퍼를 유효하게 유지한 뒤 <c>finally</c>에서 반납한다 —
    /// <see cref="PacketSendExtensions.SendAsync{T}(ISession, T, CancellationToken)"/>와 동일한 패턴.
    /// </remarks>
    private async ValueTask BroadcastPacketAsync<T>(T packet, CancellationToken cancellationToken) where T : IPacket
    {
        var buffer = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize + packet.GetBodySize());
        try
        {
            int written = _serializer.Serialize(packet, buffer);
            await _registry.BroadcastAsync(buffer.AsMemory(0, written), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
