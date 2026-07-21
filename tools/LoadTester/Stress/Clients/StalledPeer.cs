using ServerLib;
using ServerLib.Interface;

namespace LoadTester.Stress.Clients;

/// <summary>
/// Slowloris/정체 피어: TCP 연결만 맺고 인증을 완료하지 않은 채 서버 자원(세션·파이프 버퍼)을 붙잡습니다.
/// <c>silent</c> 모드는 아무것도 보내지 않고, <c>drip</c> 모드는 부분 인증 프레임을 1바이트/초로 흘려
/// 진행하지 않는 세션을 흉내냅니다. 서버에 idle sweep이 배선돼 있지 않으면 이런 세션은 영원히 누적됩니다.
/// </summary>
/// <remarks>
/// <b>[Thread Safety:]</b> <see cref="RunAsync"/> 단일 태스크. <see cref="IsHeld"/>는 volatile.
/// <b>[Blocking:]</b> Non-blocking.
/// </remarks>
public sealed class StalledPeer
{
    private readonly string _host;
    private readonly int _port;
    private readonly bool _drip;

    private volatile bool _held;

    /// <summary>정체 피어를 생성합니다.</summary>
    /// <param name="host">대상 호스트.</param>
    /// <param name="port">대상 포트.</param>
    /// <param name="drip">true=1B/s 부분 프레임 드립, false=완전 무송신.</param>
    public StalledPeer(string host, int port, bool drip)
    {
        _host = host;
        _port = port;
        _drip = drip;
    }

    /// <summary>현재 서버 자원을 붙잡고 있는지(연결 유지 중).</summary>
    public bool IsHeld => _held;

    /// <summary>연결 후 인증하지 않고(또는 느린 드립으로) 세션을 붙잡습니다. 서버가 유휴 스윕으로 끊으면
    /// (하드닝) 재접속하지 않고 종료합니다 — 하드닝 유무에 따른 서버 접속 수 차이를 그대로 드러내기 위함.</summary>
    public async Task RunAsync(CancellationToken lifetime)
    {
        var swept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using IClientConnection client = ServerNet.CreateClient();
            // 서버가 세션을 끊으면(유휴 스윕) 신호 — 재접속 없이 종료해 접속 수 감소를 지표에 노출한다.
            client.OnDisconnected = () => { _held = false; swept.TrySetResult(); return ValueTask.CompletedTask; };
            await client.ConnectAsync(_host, _port, lifetime);
            _held = true;

            if (_drip)
                _ = DripAsync(client, lifetime);

            // 서버가 끊을 때까지(또는 취소까지) 붙잡는다. 하드닝 서버는 유휴 스윕으로 끊고, 미하드닝은 무한 유지.
            await swept.Task.WaitAsync(lifetime);
        }
        catch (OperationCanceledException)
        {
            // 정상 종료.
        }
        catch (Exception)
        {
            // 연결 실패/거부(MaxConnections 등) — 조용히 포기.
        }
        finally
        {
            _held = false;
        }
    }

    private async Task DripAsync(IClientConnection client, CancellationToken lifetime)
    {
        try
        {
            byte[] partial = MalformedFrames.OversizedLengthHeader(actualBodyBytes: 200);
            int i = 0;
            while (!lifetime.IsCancellationRequested && client.IsConnected && i < partial.Length)
            {
                await client.SendAsync(partial.AsMemory(i, 1), lifetime);
                i++;
                await Task.Delay(TimeSpan.FromSeconds(1), lifetime);
            }
        }
        catch (Exception) { /* 세션이 끊기면 종료 */ }
    }
}
