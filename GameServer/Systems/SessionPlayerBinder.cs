using GameServer.Entities;
using ServerLib.Core;
using ServerLib.Interface;

namespace GameServer.Systems;

/// <summary>
/// TCP 소켓 연결을 임시 <see cref="Player"/>에 배선하는 <see cref="IServerListener"/> 콜백 모음입니다.
/// 로그인이 아직 없는 현재 단계에서, 연결될 때마다 즉시 배정 가능한 플레이어를 만들고 세션에
/// 부착하며, 해제 시 관측 이벤트를 남깁니다.
/// </summary>
/// <remarks>
/// <b>[사용 순서]</b> <see cref="OnConnected"/>/<see cref="OnDisconnected"/>/<see cref="OnError"/>를
/// <see cref="IServerListener"/>의 동일 시그니처 콜백에 메서드 그룹으로 그대로 대입해 사용합니다
/// (<c>listener.OnClientConnected = binder.OnConnected;</c> 등).
/// <br/><br/>
/// <b>[Thread Safety:]</b> Thread-safe. 이 클래스는 생성자 주입된 <see cref="PlayerLevelSystem"/>과
/// <see cref="GameEventSink"/>(둘 다 자체적으로 Thread-safe) 외에 가변 공유 상태를 갖지 않으므로,
/// 서로 다른 연결에 대해 여러 I/O 스레드가 동시에 각 메서드를 호출해도 안전합니다. 세션별 상태는
/// <see cref="ISession.Context"/>(세션마다 독립된 슬롯)에만 저장합니다.
/// <br/><br/>
/// <b>[Memory Allocation:]</b> <see cref="OnConnected"/> 호출마다 새 <see cref="Player"/> 인스턴스
/// 1개를 할당합니다(<see cref="PlayerFactory.CreateTemp"/> 위임). 세 메서드 모두
/// <see cref="ValueTask.CompletedTask"/>(캐시드, 무할당)를 반환해 콜백 자체는 추가 할당이 없습니다.
/// <br/><br/>
/// <b>[Blocking 여부:]</b> 세 메서드 모두 즉시 반환(동기, Non-blocking). DB·파일 I/O를 수행하지
/// 않습니다 — <see cref="IServerListener.OnClientConnected"/> 등은 I/O 스레드에서 직접 호출되므로,
/// 여기서 동기 블로킹을 수행하면 전체 accept/수신 루프가 지연됩니다.
/// </remarks>
public sealed class SessionPlayerBinder
{
    private readonly PlayerLevelSystem _levelSystem;
    private readonly GameEventSink _sink;

    /// <summary>지정한 레벨 시스템과 이벤트 싱크로 바인더를 생성합니다.</summary>
    /// <param name="levelSystem">임시 플레이어의 초기 레벨 스탯 적용에 사용할 시스템</param>
    /// <param name="sink">연결·해제·오류 이벤트를 기록할 싱크</param>
    public SessionPlayerBinder(PlayerLevelSystem levelSystem, GameEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(levelSystem);
        ArgumentNullException.ThrowIfNull(sink);

        _levelSystem = levelSystem;
        _sink = sink;
    }

    /// <summary>
    /// 새 연결이 수락된 직후 호출됩니다. 임시 <see cref="Player"/>를 생성해 세션에 부착합니다.
    /// </summary>
    /// <param name="session">새로 연결된 세션</param>
    /// <returns>완료된(무할당) <see cref="ValueTask"/></returns>
    /// <remarks>
    /// <c>session.SessionId</c>(<see cref="Guid"/>)를 그대로 <see cref="PlayerFactory.CreateTemp"/>에
    /// 넘깁니다 — ServerLib가 세션마다 고유하게 부여하는 값이라 로그인 없이도 충돌 없는 식별자를
    /// 얻을 수 있습니다.
    /// <br/><br/>
    /// <c>session.Context = player;</c>: <see cref="ISession.Context"/>는 세션에 내장된 단일
    /// 사용자 컨텍스트 슬롯(volatile 참조 교체, <c>DisposeAsync</c> 시 자동 null화)입니다. 별도의
    /// <c>ConcurrentDictionary&lt;Guid, Player&gt;</c> 레지스트리를 직접 관리하지 않는 이유는
    /// ServerLib가 이미 이 문제(세션별 상태 부착)를 해결해 두었기 때문입니다 — 중복 구현은
    /// 생명주기 버그(해제 누락 등)의 여지만 늘립니다.
    /// </remarks>
    public ValueTask OnConnected(ISession session)
    {
        var player = PlayerFactory.CreateTemp(session.SessionId, _levelSystem);
        session.Context = player;
        _sink.RecordPlayerConnected(player.InstanceId, player.Level);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 연결이 해제된 직후(세션당 정확히 1회) 호출됩니다. 부착돼 있던 임시 플레이어의 해제를
    /// 기록합니다.
    /// </summary>
    /// <param name="session">해제된 세션</param>
    /// <returns>완료된(무할당) <see cref="ValueTask"/></returns>
    /// <remarks>
    /// <b>Context는 이 시점에 아직 유효합니다:</b> ServerLib 소스 확인 결과
    /// (<c>SocketPipelineListener.OnDisconnected</c> 배선), <c>OnClientDisconnected</c>는
    /// <c>session.DisposeAsync()</c>보다 먼저 호출됩니다 — <c>Context</c>는 <c>DisposeAsync</c>
    /// 내부에서만 null화되므로, 이 메서드가 실행되는 동안에는 <see cref="OnConnected"/>가 부착한
    /// <see cref="Player"/>를 안전하게 읽을 수 있습니다.
    /// <br/><br/>
    /// <see cref="ServerLib.Core.SessionContextExtensions.TryGetContext{T}"/>로 방어적으로 조회합니다
    /// — <see cref="OnConnected"/>가 어떤 이유로든 호출되지 않은 이례적 세션이라도 예외 없이
    /// 건너뜁니다.
    /// <br/><br/>
    /// <b>셧다운 시점 한계:</b> 프로세스 종료 시 <c>IServerListener.Stop()</c>이 남은 세션들을
    /// 정리하는데, 그 세션들의 <c>OnClientDisconnected</c>는 수신 루프의 <c>finally</c>에서
    /// 비동기로 실행되어 <c>Stop()</c> 반환 이후에 발화할 수 있습니다. 이 시점에 이벤트 싱크가
    /// 이미 완료(<see cref="GameEventSink.CompleteWriting"/>)된 상태라면 이 기록은 조용히
    /// 유실될 수 있습니다 — 정상 동작 중(steady-state) 연결 해제는 항상 기록되지만, 프로세스
    /// 종료와 경합하는 해제는 best-effort입니다.
    /// </remarks>
    public ValueTask OnDisconnected(ISession session)
    {
        if (session.TryGetContext<Player>(out var player))
        {
            _sink.RecordPlayerDisconnected(player.InstanceId);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 세션 처리 중 예외(프로토콜 위반, 핸들러 실패 등)가 발생하면 호출됩니다.
    /// </summary>
    /// <param name="session">오류가 발생한 세션</param>
    /// <param name="exception">발생한 예외</param>
    /// <returns>완료된(무할당) <see cref="ValueTask"/></returns>
    /// <remarks>
    /// <b>현재 사이클에서는 사실상 도달하지 않습니다:</b> ServerLib 소스 확인 결과
    /// (<c>SocketPipelineSession.DispatchPacketAsync</c>), <c>OnReceived</c>가 배선되지 않은 세션은
    /// PING이 아닌 패킷을 그냥 무시하고 <see cref="ValueTask.CompletedTask"/>를 반환합니다 — 즉
    /// 패킷 본문 역직렬화 자체가 일어나지 않으므로 이 경로에서 예외가 나지 않습니다. Main.cs가
    /// 아직 <c>OnReceived</c>를 배선하지 않는 이번 사이클에는 이 콜백이 호출될 경로가 없습니다
    /// (그래서 전용 테스트도 추가하지 않았습니다 — 도달 불가능한 경로를 인위적으로 트리거하는
    /// 테스트는 다음 사이클에서 실제 프로토콜 파싱이 도입될 때 함께 작성하는 편이 낫습니다).
    /// <see cref="IServerListener"/> 콜백 계약을 온전히 구현해 두는 것은, 프로토콜이 추가되는
    /// 즉시(<c>OnReceived</c> 배선 시점) 이 경로가 자동으로 살아나게 하기 위함입니다.
    /// ServerLib는 오류 발생 시 <c>OnClientError</c>를 호출한 뒤 반드시 <c>OnClientDisconnected</c>도
    /// 호출합니다(세션 정리는 항상 뒤따름). 따라서 오류가 난 연결은 NDJSON 로그에
    /// <c>PlayerConnectionError</c>와 <c>PlayerDisconnected</c> 두 줄이 남는 것이 정상입니다.
    /// <br/><br/>
    /// <see cref="OnDisconnected"/>와 동일한 이유로 이 시점에도 <c>Context</c>는 아직 유효합니다.
    /// <see cref="Player"/>가 부착돼 있으면 그 <c>InstanceId</c>로, 아니면(예: 연결 직후 부착 전
    /// 오류) <c>session.SessionId</c> 기반 폴백 id로 기록해 오류 원인 추적이 항상 가능하게 합니다.
    /// </remarks>
    public ValueTask OnError(ISession session, Exception exception)
    {
        string playerId = session.TryGetContext<Player>(out var player)
            ? player.InstanceId
            : $"session-{session.SessionId:N}";

        _sink.RecordPlayerConnectionError(playerId, exception);
        return ValueTask.CompletedTask;
    }
}
