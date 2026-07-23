using System.Buffers.Binary;
using System.Threading.Channels;
using ServerLib;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace WebClient;

/// <summary>
/// 브라우저 접속 1건의 브리지 본체입니다: WebSocket(<see cref="IBrowserChannel"/>) ↔ GameServer
/// TCP(<see cref="IClientConnection"/>) 사이에서 <c>join → 게스트 토큰 발급 → AuthTokenPacket 제출 →
/// 인증 결과 중계 → 레이드 브로드캐스트(JSON 번역) 중계</c>를 수행하고, 어느 쪽이 끊기든 반대쪽을
/// 대칭으로 정리합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> 이 클래스 인스턴스 자체는 무상태(불변 의존성만 보유)라
/// Thread-safe — 접속마다 <see cref="RunAsync"/>를 독립 호출합니다. 접속별 상태는 전부 메서드 지역에
/// 있습니다. <c>OnReceived</c>는 ServerLib I/O 스레드에서 실행되므로 그 안에서는 채널 TryWrite만
/// 수행하고(무블로킹), WebSocket 송신은 별도 드레인 태스크 1개가 전담합니다(WS 송신 동시 1호출 계약).</description></item>
/// <item><description><b>Memory Allocation:</b> 브로드캐스트 1건당 JSON 문자열 1개(초당 ~7건 상한 —
/// MobHp 150ms 스로틀). 수신 <c>ReadOnlyMemory</c>는 콜백 동안만 유효하므로 즉시 역직렬화하고 보관하지 않습니다.</description></item>
/// <item><description><b>Blocking:</b> Non-blocking. 모든 대기가 비동기이며 취소 토큰으로 중단 가능합니다.</description></item>
/// </list>
/// </remarks>
public sealed class GameBridge
{
    /// <summary>브라우저가 WS를 연 뒤 join을 보낼 때까지 기다리는 최대 시간입니다.</summary>
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(10);

    /// <summary>AuthTokenPacket 제출 후 Ack를 기다리는 최대 시간입니다.</summary>
    private static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(5);

    // BinaryPacketSerializer: 무상태 Thread-safe 직렬화기 — 접속마다 만들지 않고 프로세스 전역 1개 공유
    // (TelemetryClientLoop·VirtualClient와 동일 패턴).
    private static readonly BinaryPacketSerializer Serializer = new();

    private readonly GuestTokenIssuer _issuer;
    private readonly GuestDirectory _directory;
    private readonly string _gameHost;
    private readonly int _gamePort;

    /// <summary>브리지를 생성합니다(프로세스 전역 1개 — 접속별 상태는 <see cref="RunAsync"/> 지역).</summary>
    /// <param name="issuer">게스트 토큰 발급기</param>
    /// <param name="directory">MVP 역매핑·정리용 게스트 디렉터리</param>
    /// <param name="gameHost">GameServer 호스트</param>
    /// <param name="gamePort">GameServer 게임 포트(기본 7777)</param>
    public GameBridge(GuestTokenIssuer issuer, GuestDirectory directory, string gameHost, int gamePort)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrEmpty(gameHost);

        _issuer = issuer;
        _directory = directory;
        _gameHost = gameHost;
        _gamePort = gamePort;
    }

    /// <summary>
    /// 브라우저 접속 1건을 끝까지(어느 한쪽 단절 또는 셧다운까지) 처리합니다. 반환 시점에는
    /// TCP·WS 양쪽이 정리되고 게스트가 디렉터리에서 제거된 상태입니다. 예외를 던지지 않습니다.
    /// </summary>
    /// <param name="browser">accept 완료된 브라우저 채널</param>
    /// <param name="lifetime">요청 중단(탭 닫힘)과 프로세스 셧다운이 연결된 토큰</param>
    public async Task RunAsync(IBrowserChannel browser, CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(browser);

        // ---- 1) join 수신 → 게스트 신원 발급 --------------------------------------------------
        GuestIdentity guest;
        try
        {
            string? first = await browser.ReceiveTextAsync(lifetime).AsTask().WaitAsync(JoinTimeout, lifetime);
            if (first is null || !BridgeMessages.TryParseJoin(first, out BridgeMessages.JoinRequest join))
            {
                await TrySendAsync(browser, new BridgeMessages.ErrorMessage("첫 메시지는 join이어야 합니다."), lifetime);
                await browser.CloseAsync(CancellationToken.None);
                return;
            }
            guest = _issuer.Issue(join.Nickname);
        }
        catch (TimeoutException)
        {
            await TrySendAsync(browser, new BridgeMessages.ErrorMessage("입장 요청 대기 시간 초과."), lifetime);
            await browser.CloseAsync(CancellationToken.None);
            return;
        }
        catch (OperationCanceledException)
        {
            return; // 탭 닫힘/셧다운 — 아직 아무것도 만든 게 없어 정리할 것도 없다.
        }

        try
        {
            await RunAuthenticatedSessionAsync(browser, guest, lifetime);
        }
        finally
        {
            // 어떤 경로로 끝나든 디렉터리에서 제거 — 이후 MVP 역매핑에서 이 계정은 익명 처리된다.
            _directory.Unregister(guest.AccountId);
        }
    }

    /// <summary>게스트 신원 확정 이후의 본 세션(접속→인증→중계→대칭 종료)을 수행합니다.</summary>
    private async Task RunAuthenticatedSessionAsync(IBrowserChannel browser, GuestIdentity guest, CancellationToken lifetime)
    {
        // Channel<string>(Bounded 256, DropOldest): 생산자는 ServerLib I/O 스레드(OnReceived)와 이 메서드,
        // 소비자는 WS 드레인 태스크 1개. I/O 스레드가 느린 브라우저의 WS 송신을 직접 기다리면 수신 루프가
        // 정체되므로 TryWrite(무블로킹)로만 넘긴다. HP바는 최신값만 의미가 있어 백로그 시 오래된 것부터
        // 버리는 DropOldest가 정당하다(용량 256이면 정상 동작에서 드롭은 발생하지 않는다).
        Channel<string> outbound = Channel.CreateBounded<string>(
            new BoundedChannelOptions(256)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest,
            });

        // 브리지 내부 종료 신호: WS 끊김·TCP 끊김·상위 lifetime 중 어느 것이든 발생하면 나머지 경로를 멈춘다.
        using var session = CancellationTokenSource.CreateLinkedTokenSource(lifetime);

        // WS 송신 드레인 태스크: 채널→WS 순차 송신으로 WebSocket "송신 동시 1호출" 계약을 보장한다.
        Task drain = Task.Run(async () =>
        {
            try
            {
                await foreach (string json in outbound.Reader.ReadAllAsync(session.Token))
                    await browser.SendTextAsync(json, session.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                // WS 송신 실패 = 브라우저 단절 — 세션 전체 종료를 트리거한다.
                session.Cancel();
            }
        }, CancellationToken.None);

        void Enqueue<T>(T message) => outbound.Writer.TryWrite(BridgeMessages.Serialize(message));

        Enqueue(new BridgeMessages.JoinedMessage(guest.Nickname, guest.AccountId));
        Enqueue(new BridgeMessages.StatusMessage("connecting"));

        // TaskCompletionSource(RunContinuationsAsynchronously): I/O 콜백 스레드가 신호만 남기고 즉시
        // 반환하도록, 후속 로직(종료 수순)은 스레드풀에서 비동기 재개한다(VirtualClient와 동일 패턴).
        var ackReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // await using: 콜백은 ConnectAsync 전에만 설정 가능(IClientConnection 계약)이라 접속마다 새 인스턴스.
        // 블록 종료 시 DisposeAsync가 TCP 소켓을 반드시 정리한다.
        await using IClientConnection game = ServerNet.CreateClient();
        game.PingInterval = TimeSpan.FromSeconds(10); // 서버 유휴 스윕(IDLERPG_GAME_IDLE_TIMEOUT_SECONDS) 대비 생존 신호.
        game.SendTimeout = TimeSpan.FromSeconds(5);

        game.OnReceived = data =>
        {
            // I/O 스레드 직접 호출 경로: 역직렬화 + JSON 변환 + TryWrite만(무블로킹).
            // ReadOnlyMemory는 반환 후 무효 — 어떤 참조도 보관하지 않는다.
            ushort packetId = BinaryPrimitives.ReadUInt16LittleEndian(data.Span);
            switch (packetId)
            {
                case AuthTokenAckPacket.Id:
                    var ack = Serializer.Deserialize<AuthTokenAckPacket>(data.Span);
                    ackReceived.TrySetResult(ack.Success);
                    break;

                case MobHpPacket.Id:
                    var hp = Serializer.Deserialize<MobHpPacket>(data.Span);
                    Enqueue(new BridgeMessages.BossHpMessage(hp.Hp, hp.MaxHp, hp.Generation));
                    break;

                case MobDeathPacket.Id:
                    var death = Serializer.Deserialize<MobDeathPacket>(data.Span);
                    // MVP instanceId → 닉네임 역매핑. 다른 프로세스(다른 WebClient·LoadTester 등)의
                    // 플레이어면 실패할 수 있다 — 그땐 instanceId 원문을 그대로 노출한다(익명 참가자).
                    bool resolved = _directory.TryResolveMvp(death.MvpName, out int mvpAccountId, out string mvpNickname);
                    Enqueue(new BridgeMessages.BossDeathMessage(
                        death.Generation,
                        death.TopDamage,
                        resolved ? mvpNickname : death.MvpName,
                        resolved && mvpAccountId == guest.AccountId));
                    break;
            }
            return ValueTask.CompletedTask;
        };
        game.OnDisconnected = () =>
        {
            disconnected.TrySetResult();
            return ValueTask.CompletedTask;
        };

        // ---- 2) TCP 접속 + 인증 ---------------------------------------------------------------
        try
        {
            await game.ConnectAsync(_gameHost, _gamePort, session.Token);
            await game.SendAsync(new AuthTokenPacket { Token = guest.Token }, session.Token);

            bool authOk = await ackReceived.Task.WaitAsync(AuthTimeout, session.Token);
            Enqueue(new BridgeMessages.AuthMessage(authOk));
            if (!authOk)
            {
                Enqueue(new BridgeMessages.ErrorMessage("GameServer 인증이 거부되었습니다(비밀키 불일치 가능성)."));
                await FinishAsync(browser, outbound, session, drain);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            await FinishAsync(browser, outbound, session, drain);
            return;
        }
        catch (TimeoutException)
        {
            Enqueue(new BridgeMessages.ErrorMessage("GameServer 인증 응답 시간 초과."));
            await FinishAsync(browser, outbound, session, drain);
            return;
        }
        catch (Exception)
        {
            Enqueue(new BridgeMessages.ErrorMessage("GameServer에 연결할 수 없습니다. 서버 기동 여부를 확인하세요."));
            await FinishAsync(browser, outbound, session, drain);
            return;
        }

        Enqueue(new BridgeMessages.StatusMessage("fighting"));

        // ---- 3) 어느 한쪽이 끊길 때까지 중계 유지 ----------------------------------------------
        // WS 감시 루프: join 이후 브라우저가 보내는 추가 메시지는 프로토콜상 없으므로 내용은 무시하고,
        // null(정상 Close·비정상 단절)만 세션 종료 신호로 쓴다.
        Task wsWatch = Task.Run(async () =>
        {
            try
            {
                while (await browser.ReceiveTextAsync(session.Token) is not null)
                {
                    // 무시 — 미래 확장(채팅 등) 전까지 유효한 후속 메시지는 없다.
                }
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        try
        {
            await Task.WhenAny(wsWatch, disconnected.Task.WaitAsync(session.Token));
        }
        catch (OperationCanceledException) { }

        // TCP가 먼저 끊긴 경우 브라우저에 마지막 상태를 알린다(드레인이 아직 살아있는 동안 enqueue).
        if (disconnected.Task.IsCompleted)
            Enqueue(new BridgeMessages.StatusMessage("disconnected"));

        await FinishAsync(browser, outbound, session, drain);
        // TCP는 이 메서드의 await using이, 게스트 해제는 호출부 finally가 마무리한다.
    }

    /// <summary>대칭 종료 수순: 잔여 메시지 드레인 → WS Close. 예외를 던지지 않습니다.</summary>
    private static async Task FinishAsync(
        IBrowserChannel browser, Channel<string> outbound, CancellationTokenSource session, Task drain)
    {
        outbound.Writer.TryComplete();
        try
        {
            // 잔여 메시지(마지막 status/error)가 브라우저에 도달할 기회를 짧게 준다 — 셧다운을 오래 붙잡지 않는다.
            await drain.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            session.Cancel(); // 드레인이 송신에 붙잡혀 있으면 강제 중단.
        }

        if (!session.IsCancellationRequested)
            session.Cancel();
        await browser.CloseAsync(CancellationToken.None);
        try
        {
            await drain; // 드레인 태스크 완주 보장(예외는 내부에서 처리됨).
        }
        catch (Exception) { }
    }

    /// <summary>단발 송신을 시도하고 실패는 무시합니다(브리지 확립 전 오류 통지 전용).</summary>
    private static async Task TrySendAsync<T>(IBrowserChannel browser, T message, CancellationToken cancellationToken)
    {
        try
        {
            await browser.SendTextAsync(BridgeMessages.Serialize(message), cancellationToken);
        }
        catch (Exception)
        {
            // 이미 끊긴 브라우저 — 통지 실패는 무해하다.
        }
    }
}
