using System.Net.WebSockets;

namespace WebClient.Tests;

/// <summary>
/// 스크립트된 <see cref="WebSocket"/> 페이크입니다. <see cref="WebClient.WebSocketBrowserChannel"/>의
/// 조각(fragment) 조립·크기 상한·종료 로직을 Kestrel/실 네트워크 없이 직접 검증하기 위한 최소 구현으로,
/// <see cref="ReceiveAsync(ArraySegment{byte}, CancellationToken)"/>가 미리 정한 프레임 시퀀스를 순서대로
/// 반환하고 <see cref="CloseAsync"/>가 받은 종료 상태 코드를 기록한다.
/// </summary>
/// <remarks>
/// <see cref="WebSocketBrowserChannel"/>은 <c>byte[]</c> 버퍼로 <c>ReceiveAsync</c>를 호출하며, 반환값을
/// <see cref="WebSocketReceiveResult"/>로 받으므로 오버로드 해석은 <see cref="ArraySegment{T}"/> 오버로드
/// (더 나은 변환 대상 규칙)로 바인딩된다 — 그 오버로드가 <c>abstract</c>이므로 반드시 여기서 재정의한다.
/// </remarks>
internal sealed class ScriptedWebSocket : WebSocket
{
    /// <summary>수신 프레임 1건의 스크립트: 채울 바이트 수·마지막 조각 여부·프레임 종류.</summary>
    public readonly record struct Frame(int Count, bool EndOfMessage, WebSocketMessageType Type);

    // Queue<Frame>: 단일 소비자(수신 루프)가 FIFO로 소진하는 스크립트 저장소 — 동기 접근만 하므로 락 불필요.
    private readonly Queue<Frame> _frames;
    private WebSocketState _state = WebSocketState.Open;

    /// <summary>지금까지 <see cref="ReceiveAsync(ArraySegment{byte}, CancellationToken)"/>가 호출된 횟수(조기 종료 검증용).</summary>
    public int ReceiveCallCount { get; private set; }

    /// <summary><see cref="CloseAsync"/>가 받은 종료 상태 코드. 미호출 시 <see langword="null"/>.</summary>
    public WebSocketCloseStatus? ClosedWith { get; private set; }

    /// <summary>주어진 프레임 시퀀스를 순서대로 반환하는 페이크를 만든다. 시퀀스 소진 후에는 정상 Close 프레임을 반환한다.</summary>
    public ScriptedWebSocket(IEnumerable<Frame> frames) => _frames = new Queue<Frame>(frames);

    public override WebSocketState State => _state;
    public override WebSocketCloseStatus? CloseStatus => ClosedWith;
    public override string? CloseStatusDescription => null;
    public override string? SubProtocol => null;

    public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReceiveCallCount++;
        Frame frame = _frames.Count > 0
            ? _frames.Dequeue()
            : new Frame(0, EndOfMessage: true, WebSocketMessageType.Close); // 스크립트 소진 = 브라우저 정상 종료.

        // 버퍼를 프레임 크기만큼 임의 텍스트 바이트로 채운다 — 내용은 무의미하고 누적 '크기'만 검증 대상이다.
        int count = Math.Min(frame.Count, buffer.Count);
        for (int i = 0; i < count; i++)
            buffer.Array![buffer.Offset + i] = (byte)'a';

        return Task.FromResult(new WebSocketReceiveResult(count, frame.Type, frame.EndOfMessage));
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        ClosedWith = closeStatus;
        _state = WebSocketState.Closed;
        return Task.CompletedTask;
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        => CloseAsync(closeStatus, statusDescription, cancellationToken);

    public override void Abort() => _state = WebSocketState.Aborted;
    public override void Dispose() { }

    public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        => Task.CompletedTask;
}
