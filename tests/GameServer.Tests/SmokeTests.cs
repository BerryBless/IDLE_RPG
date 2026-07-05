using GameServer.Entities;
using GameServer.Systems;

namespace GameServer.Tests;

/// <summary>
/// 테스트 인프라(프로젝트 참조·InternalsVisibleTo·xunit 러너)가 정상 동작하는지 확인하는 스모크 테스트.
/// 이후 A~G 단계 테스트가 이 인프라 위에서 실제 Red-Green-Refactor를 진행한다.
/// </summary>
public class SmokeTests
{
    [Fact]
    public void BattleManager_Instance_IsAccessible()
    {
        Assert.NotNull(BattleManager.Instance);
    }

    [Fact]
    public void Player_CanBeConstructed()
    {
        var player = new Player { InstanceId = "smoke-player", AccountId = 1, Level = 1 };
        Assert.Equal("smoke-player", player.InstanceId);
    }
}
