using GameServer.Entities;

namespace GameServer.Systems;

/// <summary>
/// 초기 생성 시점의 <see cref="Player"/>를 레벨 스탯 적용·자원 완충까지 마쳐 즉시 전투 투입
/// 가능한 상태로 만든다.
/// </summary>
/// <remarks>
/// 코드리뷰 2026-07-06 Medium 수정: 이전에는 <c>Main.cs</c> 등 호출부가 <c>new Player{...}</c> →
/// <see cref="PlayerLevelSystem.ApplyLevel"/> → <see cref="Entity.RestoreResources"/> 세 단계를
/// 수동으로 순서대로 호출해야 했다. 마지막 <c>RestoreResources()</c> 호출을 빠뜨리면 MaxHp는
/// 설정되지만 CurrentHp=0으로 남아 전투 시작 즉시 사망 상태가 되는 함정이 있었다.
/// <see cref="MonsterFactory.Create"/>/<see cref="Items.EquipmentFactory.Create"/>와 대칭을 맞춰
/// 이 팩토리를 신설했다.
/// <b>이 팩토리가 <see cref="PlayerLevelSystem.ApplyLevel"/> 자체를 바꾸지 않는 이유:</b>
/// <c>ApplyLevel</c>은 <see cref="BattleLoop.Tick"/>의 전투 중 레벨업 경로에도 재사용된다. 만약
/// <c>ApplyLevel</c> 내부에 <c>RestoreResources</c>를 넣으면 전투 중 레벨업할 때마다 플레이어가
/// 풀피로 회복되는 의도치 않은 부수효과가 생긴다. 그래서 이 팩토리는 "최초 생성" 전용 경로로
/// 별도로 두고, <c>ApplyLevel</c>은 그대로 둔다.
/// <b>Thread Safety:</b> Thread-safe. 정적 필드나 공유 상태가 없고, 매 호출이 완전히 독립적인
/// 새 <see cref="Player"/> 인스턴스를 생성·반환한다.
/// <b>Memory Allocation:</b> 호출 1회당 <see cref="Player"/> 인스턴스(및 기본 필드인
/// <see cref="Player.Equipment"/> 등) 1개를 새로 할당한다.
/// <b>Blocking 여부:</b> 즉시 반환(동기, non-blocking). I/O 없음.
/// </remarks>
public static class PlayerFactory
{
    /// <summary>
    /// 지정한 계정·레벨로 새 플레이어를 생성하고, 레벨 스탯을 반영한 뒤 풀피 상태로 반환한다.
    /// </summary>
    /// <param name="instanceId">플레이어 인스턴스 고유 ID</param>
    /// <param name="accountId">소유 계정 ID</param>
    /// <param name="level">적용할 초기 레벨</param>
    /// <param name="levelSystem">레벨 스탯 적용에 사용할 시스템</param>
    /// <returns>레벨 스탯이 반영되고 <see cref="Entity.RestoreResources"/>까지 호출된 새 <see cref="Player"/></returns>
    /// <exception cref="ArgumentNullException"><paramref name="levelSystem"/>이 null인 경우</exception>
    public static Player Create(string instanceId, int accountId, int level, PlayerLevelSystem levelSystem)
    {
        ArgumentNullException.ThrowIfNull(levelSystem);

        var player = new Player { InstanceId = instanceId, AccountId = accountId };
        levelSystem.ApplyLevel(player, level);
        player.RestoreResources();

        return player;
    }

    /// <summary>
    /// 로그인 없이 소켓 연결 시점에 즉시 배정할 임시 플레이어를 생성한다.
    /// </summary>
    /// <param name="sessionId">연결을 식별하는 세션 ID. <see cref="Create"/>의 <c>instanceId</c>로 변환된다.</param>
    /// <param name="levelSystem">레벨 스탯 적용에 사용할 시스템</param>
    /// <returns>레벨 1, 계정 미지정(0) 상태로 즉시 전투 투입 가능한 임시 <see cref="Player"/></returns>
    /// <exception cref="ArgumentNullException"><paramref name="levelSystem"/>이 null인 경우</exception>
    /// <remarks>
    /// <b>왜 <see cref="ISession"/>이 아니라 <see cref="Guid"/>를 받는가:</b> 이 팩토리가 ServerLib의
    /// <c>ISession</c> 타입을 직접 참조하면 GameServer의 순수 도메인 계층이 네트워크 계층에 의존하게
    /// 되어, 소켓 없이도 가능해야 할 단위 테스트가 ServerLib를 끌어와야 하는 상황이 된다. 호출부
    /// (연결 콜백)에서 <c>session.SessionId</c>(Guid)만 넘기면 되므로, 이 메서드는 ServerLib를
    /// 전혀 모르는 채로 순수 함수로 남는다.
    /// <b>AccountId=0:</b> 실 계정이 연결되지 않은 임시 플레이어의 플레이스홀더 값이다. 로그인이
    /// 구현되면 인증된 실제 계정 ID로 교체된다.
    /// <b>Thread Safety / Memory Allocation / Blocking:</b> <see cref="Create"/>와 동일 —
    /// 공유 상태 없이 완전히 독립된 새 인스턴스를 동기·비차단으로 반환한다.
    /// </remarks>
    public static Player CreateTemp(Guid sessionId, PlayerLevelSystem levelSystem)
    {
        ArgumentNullException.ThrowIfNull(levelSystem);

        // "N" 포맷(하이픈 없는 32자리 16진수): 세션마다 고유한 Guid이므로 로그인 없이도
        // 충돌 없는 InstanceId를 얻는다. 계정 미지정(0)·레벨 1 고정은 "임시 플레이어" 정책.
        var instanceId = $"player-{sessionId:N}";
        return Create(instanceId, accountId: 0, level: 1, levelSystem);
    }
}
