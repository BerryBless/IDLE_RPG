using System.Text.Json;
using System.Threading.Channels;
using WebClient;

namespace WebClient.Tests;

/// <summary>
/// <see cref="IBrowserChannel"/>의 인메모리 페이크: 테스트가 브라우저 역할을 대신한다.
/// Kestrel/WebSocket 없이 <see cref="GameBridge"/>의 전체 수명주기를 검증하기 위한 이음새 구현.
/// </summary>
public sealed class FakeBrowserChannel : IBrowserChannel
{
    // Channel<string>: 테스트 스레드(생산)와 브리지 수신 루프(소비)를 잇는 무락 큐.
    private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>();

    /// <summary>브리지가 CloseAsync를 호출했는지 여부.</summary>
    public volatile bool Closed;

    /// <summary>브라우저가 텍스트를 보낸 것처럼 주입한다.</summary>
    public void EnqueueFromBrowser(string text) => _incoming.Writer.TryWrite(text);

    /// <summary>브라우저가 탭을 닫은 것처럼(정상 Close) 수신 스트림을 끝낸다.</summary>
    public void CloseFromBrowser() => _incoming.Writer.TryComplete();

    /// <inheritdoc/>
    public async ValueTask<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        if (!await _incoming.Reader.WaitToReadAsync(cancellationToken))
            return null; // 브라우저 Close와 동일 의미.
        _incoming.Reader.TryRead(out string? text);
        return text;
    }

    /// <inheritdoc/>
    public ValueTask SendTextAsync(string text, CancellationToken cancellationToken)
    {
        _outgoing.Writer.TryWrite(text);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        Closed = true;
        _outgoing.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <summary>브리지가 보낸 메시지 중 지정 type의 첫 메시지를 기다린다(그 전 메시지는 소비·폐기).</summary>
    /// <exception cref="TimeoutException">제한 시간 내 미도착</exception>
    /// <exception cref="InvalidOperationException">채널이 닫힐 때까지 미도착</exception>
    public async Task<JsonElement> WaitForAsync(string type, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (string json in _outgoing.Reader.ReadAllAsync(cts.Token))
            {
                JsonElement root = JsonDocument.Parse(json).RootElement;
                if (root.GetProperty("type").GetString() == type)
                    return root;
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"'{type}' 메시지가 {timeout.TotalSeconds:0}초 내에 도착하지 않았습니다.");
        }
        throw new InvalidOperationException($"채널이 닫힐 때까지 '{type}' 메시지가 도착하지 않았습니다.");
    }
}
