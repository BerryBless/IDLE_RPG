using System.Text.Json;
using WebClient;

namespace WebClient.Tests;

/// <summary>브라우저 JSON 프로토콜 직렬화 계약 테스트: camelCase 필드명과 type 판별자를 고정한다
/// (GameHtml.cs의 JS가 이 이름들을 하드코딩으로 읽으므로 여기 깨지면 화면이 조용히 멈춘다).</summary>
public sealed class BridgeMessagesTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void BossHp는_camelCase와_type을_가진다()
    {
        JsonElement root = Parse(BridgeMessages.Serialize(
            new BridgeMessages.BossHpMessage(1_234_567, 5_000_000, 3)));
        Assert.Equal("bossHp", root.GetProperty("type").GetString());
        Assert.Equal(1_234_567, root.GetProperty("hp").GetInt64());
        Assert.Equal(5_000_000, root.GetProperty("maxHp").GetInt64());
        Assert.Equal(3, root.GetProperty("generation").GetInt32());
    }

    [Fact]
    public void BossDeath는_MVP_필드를_가진다()
    {
        JsonElement root = Parse(BridgeMessages.Serialize(
            new BridgeMessages.BossDeathMessage(7, 999_999, "웹용사", mvpIsMe: true)));
        Assert.Equal("bossDeath", root.GetProperty("type").GetString());
        Assert.Equal(7, root.GetProperty("generation").GetInt32());
        Assert.Equal(999_999, root.GetProperty("topDamage").GetInt64());
        Assert.Equal("웹용사", root.GetProperty("mvpName").GetString());
        Assert.True(root.GetProperty("mvpIsMe").GetBoolean());
    }

    [Fact]
    public void Joined_Auth_Status_Error도_type_판별자를_가진다()
    {
        Assert.Equal("joined", Parse(BridgeMessages.Serialize(new BridgeMessages.JoinedMessage("닉", 1_000_000))).GetProperty("type").GetString());
        Assert.Equal("auth", Parse(BridgeMessages.Serialize(new BridgeMessages.AuthMessage(true))).GetProperty("type").GetString());
        Assert.Equal("status", Parse(BridgeMessages.Serialize(new BridgeMessages.StatusMessage("fighting"))).GetProperty("type").GetString());
        Assert.Equal("error", Parse(BridgeMessages.Serialize(new BridgeMessages.ErrorMessage("사유"))).GetProperty("type").GetString());
    }

    [Fact]
    public void 유효한_join은_파싱된다()
    {
        Assert.True(BridgeMessages.TryParseJoin("""{"type":"join","nickname":"용사"}""", out var join));
        Assert.Equal("용사", join.Nickname);
    }

    [Fact]
    public void 닉네임_없는_join도_유효하다()
    {
        Assert.True(BridgeMessages.TryParseJoin("""{"type":"join"}""", out var join));
        Assert.Null(join.Nickname);
    }

    [Theory]
    [InlineData("""{"type":"attack"}""")]  // 다른 타입
    [InlineData("""{"nickname":"x"}""")]   // type 누락
    [InlineData("not-json-at-all")]        // JSON 아님
    [InlineData("null")]                   // JSON null
    [InlineData("")]                       // 빈 문자열
    public void 유효하지_않은_join은_거부된다(string text)
    {
        Assert.False(BridgeMessages.TryParseJoin(text, out _));
    }
}
