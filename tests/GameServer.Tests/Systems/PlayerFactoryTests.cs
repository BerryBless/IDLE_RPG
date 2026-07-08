using GameServer.Systems;

namespace GameServer.Tests.Systems;

public class PlayerFactoryTests
{
    private static readonly PlayerLevelSystem LevelSystem = PlayerLevelSystem.CreateDefault();

    [Fact]
    public void Create_SetsInstanceIdAndAccountId()
    {
        var player = PlayerFactory.Create("p-1", accountId: 42, level: 1, LevelSystem);

        Assert.Equal("p-1", player.InstanceId);
        Assert.Equal(42, player.AccountId);
    }

    [Fact]
    public void Create_AppliesLevelStats()
    {
        var player = PlayerFactory.Create("p-1", accountId: 1, level: 3, LevelSystem); // Lv3: Hp170, Atk19, Def5

        Assert.Equal(3, player.Level);
        Assert.Equal(170, player.BaseStats.Hp);
        Assert.Equal(19, player.BaseStats.Atk);
    }

    [Fact]
    public void Create_ReturnsPlayerImmediatelyAliveAtFullHp()
    {
        // 코드리뷰 2026-07-06 Medium 수정: RestoreResources 호출 누락 함정을 팩토리로 해소했는지 검증.
        var player = PlayerFactory.Create("p-1", accountId: 1, level: 1, LevelSystem);

        Assert.True(player.IsAlive);
        Assert.Equal(player.FinalStats.MaxHp, player.FinalStats.CurrentHp);
    }

    [Fact]
    public void Create_NullLevelSystem_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PlayerFactory.Create("p-1", accountId: 1, level: 1, null!));
    }

    [Fact]
    public void CreateTemp_DerivesInstanceIdFromSessionId()
    {
        var sessionId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var player = PlayerFactory.CreateTemp(sessionId, LevelSystem);

        // "N" 포맷: 하이픈 없는 32자리 16진수. ISession.SessionId(Guid)는 세션마다 고유하므로
        // 로그인 없이도 충돌 없는 InstanceId를 얻을 수 있다.
        Assert.Equal($"player-{sessionId:N}", player.InstanceId);
    }

    [Fact]
    public void CreateTemp_SetsAccountIdZeroAndLevelOne()
    {
        var player = PlayerFactory.CreateTemp(Guid.NewGuid(), LevelSystem);

        // AccountId=0: 실 계정이 없는 임시 플레이어의 플레이스홀더(실제 로그인 구현 시 교체 대상).
        Assert.Equal(0, player.AccountId);
        Assert.Equal(1, player.Level);
    }

    [Fact]
    public void CreateTemp_ReturnsPlayerImmediatelyAliveAtFullHp()
    {
        var player = PlayerFactory.CreateTemp(Guid.NewGuid(), LevelSystem);

        Assert.True(player.IsAlive);
        Assert.Equal(player.FinalStats.MaxHp, player.FinalStats.CurrentHp);
    }

    [Fact]
    public void CreateTemp_NullLevelSystem_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PlayerFactory.CreateTemp(Guid.NewGuid(), null!));
    }
}
