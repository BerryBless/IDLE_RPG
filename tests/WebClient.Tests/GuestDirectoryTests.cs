using WebClient;

namespace WebClient.Tests;

/// <summary>MVP instanceId(<c>player-{accountId}-{sessionId}</c>) 역매핑 파싱 규칙 테스트.
/// GameServer <c>SessionAuthGate</c>의 instanceId 포맷 의존을 고정하는 안전망이다.</summary>
public sealed class GuestDirectoryTests
{
    [Fact]
    public void 등록된_계정의_instanceId는_닉네임으로_역매핑된다()
    {
        var directory = new GuestDirectory();
        directory.Register(1_000_007, "웹용사");

        // SessionAuthGate.cs: $"player-{claims.AccountId}-{session.SessionId:N}" 실제 포맷 재현.
        Assert.True(directory.TryResolveMvp(
            $"player-1000007-{Guid.NewGuid():N}", out int accountId, out string nickname));
        Assert.Equal(1_000_007, accountId);
        Assert.Equal("웹용사", nickname);
    }

    [Fact]
    public void 미등록_계정은_실패한다()
    {
        var directory = new GuestDirectory();
        Assert.False(directory.TryResolveMvp("player-42-abc", out _, out _));
    }

    [Fact]
    public void 해제된_계정은_더_이상_역매핑되지_않는다()
    {
        var directory = new GuestDirectory();
        directory.Register(1_000_001, "곧떠남");
        directory.Unregister(1_000_001);
        Assert.False(directory.TryResolveMvp("player-1000001-abc", out _, out _));
    }

    [Theory]
    [InlineData("없음")]                        // MVP 없는 세대(서버 기본 문자열)
    [InlineData("mob-7001")]                    // 접두사 불일치
    [InlineData("player-")]                     // accountId 없음
    [InlineData("player-abc-def")]              // 숫자 아님
    [InlineData("player-123")]                  // 구분 대시 없음(세션ID 누락)
    [InlineData("")]                            // 빈 문자열
    public void 포맷이_다르면_실패한다(string mvpName)
    {
        var directory = new GuestDirectory();
        directory.Register(123, "x");
        Assert.False(directory.TryResolveMvp(mvpName, out _, out _));
    }

    [Fact]
    public void 재등록은_닉네임을_덮어쓴다()
    {
        var directory = new GuestDirectory();
        directory.Register(1_000_002, "이전");
        directory.Register(1_000_002, "이후");
        Assert.True(directory.TryResolveMvp("player-1000002-x1", out _, out string nickname));
        Assert.Equal("이후", nickname);
    }
}
