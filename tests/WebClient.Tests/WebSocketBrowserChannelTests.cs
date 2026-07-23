using System.Net.WebSockets;
using WebClient;
using static WebClient.Tests.ScriptedWebSocket;

namespace WebClient.Tests;

/// <summary>
/// 실 WebSocket 어댑터 <see cref="WebSocketBrowserChannel"/>의 수신 조립·크기 상한·프레임 종류 처리
/// 단위 테스트. <see cref="ScriptedWebSocket"/> 페이크로 Kestrel 없이 어댑터 코드를 직접 실행한다.
/// </summary>
public sealed class WebSocketBrowserChannelTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // 조각 총 바이트가 상한(16KB)을 넘으면 즉시 1009(MessageTooBig)로 끊고 수신을 끝낸다 —
    // 무제한 MemoryStream 증폭 DoS 차단(코드 리뷰 High 결함).
    [Fact]
    public async Task ReceiveText_OversizedFragmentStream_AbortsWithMessageTooBig()
    {
        // 4KB 조각 10개(=40KB, 상한 16KB 초과)를 EndOfMessage 없이 흘려보낸다.
        var frames = Enumerable.Repeat(new Frame(4096, EndOfMessage: false, WebSocketMessageType.Text), 10);
        var socket = new ScriptedWebSocket(frames);
        var channel = new WebSocketBrowserChannel(socket);

        using var cts = new CancellationTokenSource(Timeout);
        string? result = await channel.ReceiveTextAsync(cts.Token);

        Assert.Null(result); // 조립된 거대 문자열이 아니라 수신 종료로 처리되어야 한다.
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.ClosedWith);
        Assert.True(socket.ReceiveCallCount <= 6, $"상한 초과 즉시 중단해야 하는데 {socket.ReceiveCallCount}회 수신함");
    }

    // 크기는 작지만(빈/작은 조각) 개수만 무한한 조각 플러드도 개수 상한으로 끊는다 —
    // 바이트 캡만으로는 막지 못하는 무한 루프/hang 차단(인증 후 wsWatch 루프 방어).
    [Fact]
    public async Task ReceiveText_ManyTinyFragments_AbortsWithMessageTooBig()
    {
        // 1바이트 조각 200개: 총 200바이트라 바이트 캡(16KB)엔 안 닿지만 개수 캡을 넘긴다.
        var frames = Enumerable.Repeat(new Frame(1, EndOfMessage: false, WebSocketMessageType.Text), 200);
        var socket = new ScriptedWebSocket(frames);
        var channel = new WebSocketBrowserChannel(socket);

        using var cts = new CancellationTokenSource(Timeout);
        string? result = await channel.ReceiveTextAsync(cts.Token);

        Assert.Null(result);
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.ClosedWith);
        Assert.True(socket.ReceiveCallCount < 200, $"개수 상한 전에 끊어야 하는데 {socket.ReceiveCallCount}회 수신함");
    }

    // 단일 프레임(정상 join 경로): 상한 도입이 정상 트래픽을 깨뜨리지 않는지 회귀 검증.
    [Fact]
    public async Task ReceiveText_SingleFrame_ReturnsDecodedText()
    {
        var frames = new[] { new Frame(50, EndOfMessage: true, WebSocketMessageType.Text) };
        var socket = new ScriptedWebSocket(frames);
        var channel = new WebSocketBrowserChannel(socket);

        using var cts = new CancellationTokenSource(Timeout);
        string? result = await channel.ReceiveTextAsync(cts.Token);

        Assert.Equal(new string('a', 50), result);
        Assert.Null(socket.ClosedWith); // 정상 수신은 종료를 유발하지 않는다.
    }

    // 상한 이내 다중 조각: 합법적으로 조각난 메시지(<16KB)는 온전히 조립되어야 한다.
    [Fact]
    public async Task ReceiveText_MultipleFragmentsWithinLimit_AssemblesFully()
    {
        var frames = new[]
        {
            new Frame(4096, EndOfMessage: false, WebSocketMessageType.Text),
            new Frame(4096, EndOfMessage: false, WebSocketMessageType.Text),
            new Frame(1000, EndOfMessage: true, WebSocketMessageType.Text),
        };
        var socket = new ScriptedWebSocket(frames);
        var channel = new WebSocketBrowserChannel(socket);

        using var cts = new CancellationTokenSource(Timeout);
        string? result = await channel.ReceiveTextAsync(cts.Token);

        Assert.Equal(new string('a', 9192), result);
        Assert.Null(socket.ClosedWith);
    }

    // 바이너리 프레임은 이 텍스트 전용 프로토콜의 위반 — 수신 종료(null)로 취급.
    [Fact]
    public async Task ReceiveText_BinaryFrame_ReturnsNull()
    {
        var frames = new[] { new Frame(10, EndOfMessage: true, WebSocketMessageType.Binary) };
        var socket = new ScriptedWebSocket(frames);
        var channel = new WebSocketBrowserChannel(socket);

        using var cts = new CancellationTokenSource(Timeout);
        string? result = await channel.ReceiveTextAsync(cts.Token);

        Assert.Null(result);
    }

    // 상대 정상 Close 프레임 → null 반환.
    [Fact]
    public async Task ReceiveText_CloseFrame_ReturnsNull()
    {
        var frames = new[] { new Frame(0, EndOfMessage: true, WebSocketMessageType.Close) };
        var socket = new ScriptedWebSocket(frames);
        var channel = new WebSocketBrowserChannel(socket);

        using var cts = new CancellationTokenSource(Timeout);
        string? result = await channel.ReceiveTextAsync(cts.Token);

        Assert.Null(result);
    }
}
