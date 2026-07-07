using GameServer.Entities;

namespace GameServer.Systems;

/// <summary>
/// 샤드 전용 스레드 안에서 <see cref="BattleLoop.Tick"/> 1회 호출을 예외로부터 격리한다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Context:</b> 다중 플레이어 배틀 샤드 전용 <see cref="System.Threading.Thread"/>에서
/// 동기 호출되는 것을 전제로 한다.</description></item>
/// <item><description><b>Thread Safety:</b> <see cref="TryTick"/> 자체는 공유 상태가 없어 여러 스레드가
/// 동시에 호출해도 안전하다. 다만 인자로 전달하는 <paramref name="loop"/>는 내부 상태 없이 읽기 전용
/// 조회만 수행하는 <see cref="BattleLoop"/>이어야 하며, 같은 <see cref="Player"/>/<see cref="Monster"/>
/// 인스턴스를 여러 스레드에서 동시에 대상으로 호출해서는 안 된다(<see cref="BattleLoop.Tick"/> 자체의
/// 제약과 동일).</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="TryTick"/> 자체는 새 힙 객체를 만들지 않는다.
/// 정상 경로의 할당은 <see cref="BattleLoop.Tick"/>이 이미 수행하는 것이 전부이며, 예외 경로에서는
/// <c>catch</c>로 잡은 기존 예외 객체를 그대로 반환할 뿐 새로 생성하지 않는다.</description></item>
/// <item><description><b>Blocking 여부:</b> Non-blocking. <see cref="BattleLoop.Tick"/>과 동일하게 즉시
/// 반환한다.</description></item>
/// </list>
/// <b>[왜 이 래퍼가 필요한가]</b> 전용 <see cref="System.Threading.Thread"/>에서 처리되지 않은 예외가
/// 발생하면 .NET 런타임은 백그라운드 스레드 여부와 무관하게 프로세스 전체를 종료시킨다. 한 플레이어의
/// <see cref="BattleLoop.Tick"/> 호출에서 발생한 예외가 같은 샤드/다른 샤드의 나머지 플레이어까지 전부
/// 죽이지 않도록, 쌍(pair) 단위로 예외를 잡아 호출 측에 반환한다.
/// </remarks>
public static class ShardBattleRunner
{
    /// <summary>
    /// <paramref name="loop"/>의 <c>Tick</c>을 호출하고, 예외가 발생하면 잡아서
    /// <paramref name="exception"/>으로 반환한다.
    /// </summary>
    /// <param name="loop">전투 로직을 수행할 <see cref="BattleLoop"/>(여러 샤드가 공유 가능)</param>
    /// <param name="player">이번 틱에 참여하는 플레이어</param>
    /// <param name="monster">이번 틱에 참여하는 몬스터(플레이어 전용 인스턴스여야 함)</param>
    /// <param name="deltaTime">이번 틱에 해당하는 경과 시간(초)</param>
    /// <param name="exception"><c>Tick</c> 호출 중 발생한 예외. 정상 처리됐다면 null.</param>
    /// <returns>
    /// 정상 처리됐다면 <see cref="BattleTickEvent"/>, 예외가 발생했다면 null
    /// (이 경우 <paramref name="exception"/>이 null이 아니다).
    /// </returns>
    public static BattleTickEvent? TryTick(BattleLoop loop, Player player, Monster monster, float deltaTime, out Exception? exception)
    {
        try
        {
            var result = loop.Tick(player, monster, deltaTime);
            exception = null;
            return result;
        }
        catch (Exception ex)
        {
            exception = ex;
            return null;
        }
    }
}
