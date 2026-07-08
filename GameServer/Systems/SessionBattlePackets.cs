using GameServer.Entities;
using ServerLib.Core.Serialization.Packets;

namespace GameServer.Systems;

/// <summary>
/// 한 틱의 전투 결과(<see cref="BattleTickEvent"/> + <see cref="Player"/>/<see cref="Monster"/> 상태)를
/// 세션에 전송할 <see cref="MobHpPacket"/>/<see cref="MobDeathPacket"/>으로 매핑한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 정적 메서드이고 공유 가변 상태가 없다 —
/// 인자로 받은 값만으로 순수하게 결과를 계산한다. 여러 세션의 전투 루프가 동시에 호출해도 안전하다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="MobHpPacket"/>은 struct(무할당). 몬스터가
/// 처치된 틱에만 <see cref="MobDeathPacket"/>(class) 1개를 추가로 할당한다 — 처치는 저빈도 이벤트라
/// GC 영향은 무시할 수 있다(패킷 자체의 설계 의도와 동일).</description></item>
/// <item><description><b>Blocking 여부:</b> Non-blocking. 소켓·I/O를 전혀 다루지 않는 순수 함수라
/// <see cref="ServerLib"/>의 <c>ISession</c>을 알 필요가 없다 — <see cref="BattleLoop"/>와 동일하게
/// 네트워크 계층과 분리된 도메인 계층에 둔다.</description></item>
/// </list>
/// </remarks>
internal static class SessionBattlePackets
{
    /// <summary>
    /// <see cref="BuildTickPackets"/>의 반환값. 이번 틱에 보낼 패킷들과 다음 틱에 사용할 세대 번호를 담는다.
    /// </summary>
    /// <param name="Death">몬스터가 이번 틱에 처치되었으면 전송할 사망 패킷, 아니면 <see langword="null"/>.</param>
    /// <param name="Hp">이번 틱에 항상 전송하는 HP 패킷(처치 시에는 재등장 이후의 새 세대 값).</param>
    /// <param name="NextGeneration">다음 틱 호출 시 <c>currentGeneration</c>으로 넘겨야 할 값.</param>
    internal readonly record struct TickPacketSet(MobDeathPacket? Death, MobHpPacket Hp, int NextGeneration);

    /// <summary>
    /// 한 틱의 전투 결과를 세션에 보낼 패킷들로 변환한다.
    /// </summary>
    /// <param name="evt"><see cref="BattleLoop.Tick"/>이 반환한 이번 틱의 이벤트</param>
    /// <param name="player">전투에 참여한 플레이어(1인 전투의 유일한 기여자로 취급)</param>
    /// <param name="monster">전투 대상 몬스터. <see cref="BattleTickEvent.MonsterDefeated"/>인 경우
    /// <see cref="BattleLoop.Tick"/>이 이미 <c>RestoreResources</c>로 재등장(풀피)시킨 이후의 상태다.</param>
    /// <param name="currentGeneration">호출 시점까지 유지해 온 세대 번호(처음 호출은 1로 시작)</param>
    /// <returns>이번 틱에 보낼 패킷들과 다음 틱을 위한 세대 번호</returns>
    /// <remarks>
    /// <b>세대(Generation) 규칙:</b> <see cref="MobHpPacket.Generation"/>은 클라이언트가 "이 HP가 어느
    /// 몬스터 인스턴스의 것인가"를 구분하는 값이다. 처치 이벤트가 나면 사망 패킷은 처치된(=현재)
    /// 세대 번호로, 그 직후 전송하는 HP 패킷은 이미 재등장한 다음 세대 번호로 보낸다.
    /// <b>1인 전투 MVP 관례:</b> 공유 보스가 아직 없는 이번 사이클에는 몬스터를 죽인 것이 항상
    /// 그 세션의 플레이어 1명뿐이므로, <see cref="MobDeathPacket.MvpName"/>은 <c>player.InstanceId</c>,
    /// <see cref="MobDeathPacket.TopDamage"/>는 몬스터의 최대 HP 전량(=유일 기여자의 100% 기여)으로
    /// 고정한다. 향후 공유 보스 사이클에서는 실제 누적 데미지 기반 MVP로 대체될 값이다.
    /// <b>HP 클램프:</b> <c>(long)Math.Max(0, monster.FinalStats.CurrentHp)</c> — 부동소수 계산
    /// 오차로 아주 작은 음수가 나오더라도 클라이언트는 항상 0 이상의 HP만 받는다
    /// (<see cref="MobHpPacket"/> 자신의 문서화된 보장과 동일).
    /// </remarks>
    internal static TickPacketSet BuildTickPackets(BattleTickEvent evt, Player player, Monster monster, int currentGeneration)
    {
        long maxHp = (long)monster.FinalStats.MaxHp;

        if (evt == BattleTickEvent.MonsterDefeated)
        {
            var death = new MobDeathPacket
            {
                Generation = currentGeneration,
                TopDamage = maxHp,
                MvpName = player.InstanceId
            };

            int nextGeneration = currentGeneration + 1;
            var respawnedHp = new MobHpPacket
            {
                Hp = (long)Math.Max(0, monster.FinalStats.CurrentHp),
                MaxHp = maxHp,
                Generation = nextGeneration
            };

            return new TickPacketSet(death, respawnedHp, nextGeneration);
        }

        var hp = new MobHpPacket
        {
            Hp = (long)Math.Max(0, monster.FinalStats.CurrentHp),
            MaxHp = maxHp,
            Generation = currentGeneration
        };

        return new TickPacketSet(null, hp, currentGeneration);
    }
}
