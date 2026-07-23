namespace WebClient;

/// <summary>
/// 브라우저와의 텍스트 메시지 채널 추상화입니다. 프로덕션은 <see cref="WebSocketBrowserChannel"/>
/// (실 WebSocket)이고, 테스트는 인메모리 페이크로 대체해 <see cref="GameBridge"/>를 Kestrel 없이
/// 검증합니다 — 이 인터페이스가 브리지 로직과 웹 전송 계층 사이의 유일한 이음새입니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> <see cref="ReceiveTextAsync"/>는 단일 소비자,
/// <see cref="SendTextAsync"/>는 단일 생산자 전제입니다(WebSocket 계약: 방향별 동시 1호출).
/// <see cref="GameBridge"/>가 수신 루프 1개·송신 드레인 태스크 1개로 이 계약을 지킵니다.</description></item>
/// <item><description><b>Memory Allocation:</b> 수신마다 문자열 1개 할당(저빈도 — 브라우저는 join 1건만 보냄).</description></item>
/// <item><description><b>Blocking:</b> 전 메서드 비동기(Non-blocking). 취소 토큰으로 대기를 중단할 수 있습니다.</description></item>
/// </list>
/// </remarks>
public interface IBrowserChannel
{
    /// <summary>브라우저가 보낸 다음 텍스트 메시지를 수신합니다.</summary>
    /// <param name="cancellationToken">대기 중단 토큰</param>
    /// <returns>텍스트 프레임 1건. 브라우저가 정상 Close했으면 <see langword="null"/></returns>
    /// <exception cref="OperationCanceledException">토큰 취소 시</exception>
    ValueTask<string?> ReceiveTextAsync(CancellationToken cancellationToken);

    /// <summary>브라우저로 텍스트 메시지 1건을 송신합니다.</summary>
    /// <param name="text">보낼 JSON 텍스트</param>
    /// <param name="cancellationToken">송신 중단 토큰</param>
    ValueTask SendTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>채널을 정상 종료(Close 핸드셰이크)합니다. 이미 닫혔으면 조용히 무시합니다.</summary>
    /// <param name="cancellationToken">핸드셰이크 대기 중단 토큰</param>
    ValueTask CloseAsync(CancellationToken cancellationToken);
}
