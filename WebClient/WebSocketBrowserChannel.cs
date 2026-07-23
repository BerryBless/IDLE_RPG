using System.Net.WebSockets;
using System.Text;

namespace WebClient;

/// <summary>
/// <see cref="IBrowserChannel"/>의 실 WebSocket 어댑터입니다. Kestrel이 accept한
/// <see cref="WebSocket"/>을 감싸 텍스트 프레임 단위 수신/송신으로 변환합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> .NET <see cref="WebSocket"/> 계약과 동일 — 수신 1·송신 1의
/// 방향별 단일 동시성만 허용합니다. <see cref="GameBridge"/>가 이 계약을 보장합니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 수신 버퍼 4KB 1회(생성 시) + 메시지당 문자열 1개.
/// 브라우저→서버는 join 1건뿐이라 풀링이 불필요한 저빈도 경로입니다.</description></item>
/// <item><description><b>Blocking:</b> 전 메서드 비동기(Non-blocking).</description></item>
/// </list>
/// </remarks>
public sealed class WebSocketBrowserChannel : IBrowserChannel
{
    private readonly WebSocket _socket;

    // 4KB 고정 수신 버퍼: 기대 수신은 join JSON 1건(수십 바이트)뿐 — 조각(fragment) 수신 시에만
    // MemoryStream으로 승격해 이어 붙인다(아래 ReceiveTextAsync). ArrayPool 미사용은 채널 수명
    // 전체(접속당 1개)로 버퍼가 살아 있어 대여-반납 사이클이 성립하지 않기 때문.
    private readonly byte[] _receiveBuffer = new byte[4096];

    /// <summary>Kestrel이 accept한 WebSocket을 감싸는 채널을 생성합니다.</summary>
    /// <param name="socket">accept 완료 상태의 소켓(소유권은 호출부 — using 범위 안에서만 사용)</param>
    public WebSocketBrowserChannel(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        _socket = socket;
    }

    /// <inheritdoc/>
    public async ValueTask<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        // 대부분의 메시지는 단일 프레임으로 끝난다. EndOfMessage=false(조각)일 때만 스트림 승격.
        MemoryStream? assembled = null;
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(_receiveBuffer, cancellationToken);
            }
            catch (WebSocketException)
            {
                return null; // 비정상 단절(탭 강제 종료 등)도 정상 Close와 동일하게 "수신 끝"으로 취급.
            }

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // 이 프로토콜은 텍스트 전용 — 바이너리는 프로토콜 위반이므로 수신 종료로 취급한다.
                return null;
            }

            if (result.EndOfMessage && assembled is null)
                return Encoding.UTF8.GetString(_receiveBuffer, 0, result.Count);

            assembled ??= new MemoryStream();
            assembled.Write(_receiveBuffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(assembled.GetBuffer(), 0, (int)assembled.Length);
        }
    }

    /// <inheritdoc/>
    public async ValueTask SendTextAsync(string text, CancellationToken cancellationToken)
    {
        byte[] payload = Encoding.UTF8.GetBytes(text);
        await _socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    /// <inheritdoc/>
    public async ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            return;
        try
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cancellationToken);
        }
        catch (WebSocketException)
        {
            // 상대가 이미 끊었으면 핸드셰이크가 실패할 수 있다 — 종료 경로이므로 조용히 무시.
        }
        catch (OperationCanceledException)
        {
            // 셧다운 중 취소 — 종료 경로이므로 조용히 무시.
        }
    }
}
