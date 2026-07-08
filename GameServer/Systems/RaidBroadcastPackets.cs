using ServerLib.Core.Serialization.Packets;

namespace GameServer.Systems;

/// <summary>
/// 공유 레이드 보스의 <see cref="RaidStepBroadcast"/>(<see cref="RaidEncounter.RunAsync"/>의 onStep
/// 콜백이 넘기는 순수 도메인 값)를 전 세션에 브로드캐스트할 <see cref="MobHpPacket"/>/
/// <see cref="MobDeathPacket"/>으로 매핑한다. 사이클 1의 <c>SessionBattlePackets</c>와 동형인
/// 소켓 없는 순수 매퍼 — <see cref="RaidEncounter"/>와 마찬가지로 ServerLib의 <c>ISession</c>은
/// 전혀 모르고 패킷 타입만 참조한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. 정적 메서드이고 공유 가변 상태가 없다 —
/// 인자로 받은 <see cref="RaidStepBroadcast"/> 값만으로 순수하게 결과를 계산한다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="MobHpPacket"/>은 struct(무할당). 처치
/// (<see cref="RaidEventType.BossDefeated"/>) 스텝에서만 <see cref="MobDeathPacket"/>(class) 1개를
/// 추가로 할당한다 — 처치는 저빈도 이벤트라 GC 영향은 무시할 수 있다.</description></item>
/// <item><description><b>Blocking 여부:</b> Non-blocking. 소켓·I/O를 전혀 다루지 않는 순수 함수다.</description></item>
/// </list>
/// </remarks>
internal static class RaidBroadcastPackets
{
    /// <summary>
    /// 레이드 액터의 스텝 결과를 전 세션 브로드캐스트용 패킷들로 변환한다.
    /// </summary>
    /// <param name="info">액터가 넘긴 이번 스텝의 순수 브로드캐스트 값</param>
    /// <returns>
    /// <see cref="RaidEventType.BossDefeated"/>일 때만 <c>Death</c>가 채워진다(그 외에는 <see
    /// langword="null"/>). <c>Hp</c>는 모든 이벤트에서 항상 채워진다 — 호출자(네트워크 계층)가
    /// <see cref="RaidEventType.None"/>/<see cref="RaidEventType.BossDamaged"/>에 대해 HP 브로드캐스트를
    /// 스로틀링할지 여부는 이 매퍼의 책임이 아니다.
    /// </returns>
    /// <remarks>
    /// <b>세대(Generation) 규칙:</b> 처치(<see cref="RaidEventType.BossDefeated"/>)면 사망 패킷은
    /// <c>info.DeadGeneration</c>(방금 끝난 세대)으로, 뒤이은 HP 패킷은 <c>info.NewGeneration</c>
    /// (이미 재등장한 다음 세대)으로 보낸다. 그 외 이벤트(<see cref="RaidEventType.RaidFailed"/>
    /// 포함)는 사망 패킷 없이 HP 패킷만 <c>info.NewGeneration</c>으로 보낸다 — 실패도 보스를
    /// 리셋시키므로 세대가 이미 증가해 있다(MVP 없이 세대만 갱신).
    /// <b>HP 클램프:</b> <c>Math.Max(0, info.CurrentHp)</c> — <see cref="RaidStepBroadcast"/> 생성
    /// 시점(<see cref="RaidEncounter"/> 내부)에서 이미 클램프됐지만, 이 매퍼도 방어적으로 한 번 더
    /// 클램프해 계약을 명시적으로 지킨다(<c>SessionBattlePackets.BuildTickPackets</c>와 동일 관례).
    /// </remarks>
    internal static (MobDeathPacket? Death, MobHpPacket Hp) Build(RaidStepBroadcast info)
    {
        long clampedHp = Math.Max(0, info.CurrentHp);

        if (info.Event == RaidEventType.BossDefeated)
        {
            var death = new MobDeathPacket
            {
                Generation = info.DeadGeneration,
                TopDamage = info.TopDamage,
                MvpName = info.MvpName
            };

            var respawnedHp = new MobHpPacket
            {
                Hp = clampedHp,
                MaxHp = info.MaxHp,
                Generation = info.NewGeneration
            };

            return (death, respawnedHp);
        }

        var hp = new MobHpPacket
        {
            Hp = clampedHp,
            MaxHp = info.MaxHp,
            Generation = info.NewGeneration
        };

        return (null, hp);
    }
}
