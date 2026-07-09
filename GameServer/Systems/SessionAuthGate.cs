using GameServer.Entities;
using ServerLib.Core;
using ServerLib.Core.Auth;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace GameServer.Systems;

/// <summary>
/// <see cref="AuthTokenPacket"/>(AuthServer가 발급한 HMAC 토큰)을 검증해 성공한 세션에만 실제
/// <see cref="Player"/>(인증된 계정 ID)를 결합하는 강한 인증 관문입니다. 검증 전까지는 어떤
/// <see cref="Player"/>도 만들지 않으며, <see cref="PlayerFactory.CreateTemp"/> 기반의 즉시-참전
/// 임시 플레이어 경로(<see cref="SessionPlayerBinder.OnConnected"/>)를 대체합니다.
/// </summary>
/// <remarks>
/// <b>[사용 순서]</b> <see cref="HandleAsync"/>를 <see cref="IServerListener.OnReceived"/>에 배선합니다.
/// 반환값이 <c>true</c>인 호출(=이번 호출에서 새로 인증 성공)에서만 호출자가 이어서 레이드 참전
/// (<c>SessionRaidRunner.OnConnected</c>)을 시작해야 합니다 — <see cref="OnReceived"/>과 시그니처가
/// 다르므로(<c>bool</c> 반환) 직접 대입하지 않고 <c>Main.cs</c>가 얇은 람다로 감쌉니다.
/// <br/><br/>
/// <b>[Thread Safety:]</b> Thread-safe. 생성자 주입 의존성(검증기·레벨 시스템·싱크·직렬화기)이 모두
/// Thread-safe이고 이 클래스 자체는 가변 공유 상태를 갖지 않으므로, 서로 다른 세션에 대해 여러 I/O
/// 스레드가 동시에 호출해도 안전합니다. 세션별 상태는 오직 <see cref="ISession.Context"/>/
/// <see cref="ISession.State"/>에만 기록됩니다.
/// <br/><br/>
/// <b>[Memory Allocation:]</b> 검증 성공 시 <see cref="Player"/> 인스턴스 1개, <see cref="AuthTokenPacket"/>
/// 역직렬화로 인한 Token string 1개, 응답 <see cref="AuthTokenAckPacket"/> 전송용 풀 버퍼(반납됨)가
/// 할당됩니다 — 로그인은 세션당 1회뿐인 저빈도 경로이므로 허용 범위입니다.
/// <br/><br/>
/// <b>[Blocking 여부:]</b> Non-blocking. <see cref="IAuthTokenValidator.TryValidate"/>는 DB 조회 없이
/// 순수 HMAC 재계산만 수행하므로(무상태 코덱) 이 메서드는 네트워크 IO 스레드를 블로킹하지 않습니다.
/// </remarks>
public sealed class SessionAuthGate
{
    private readonly IAuthTokenValidator _validator;
    private readonly PlayerLevelSystem _levelSystem;
    private readonly GameEventSink _sink;
    private readonly BinaryPacketSerializer _serializer;

    /// <summary>의존성을 주입하여 인증 게이트를 생성합니다.</summary>
    /// <param name="validator">AuthServer와 공유하는 HMAC 비밀키로 토큰을 검증할 검증기</param>
    /// <param name="levelSystem">인증된 플레이어의 초기 레벨 스탯 적용에 사용할 시스템</param>
    /// <param name="sink">인증 성공 이벤트를 기록할 싱크</param>
    /// <param name="serializer">패킷 (역)직렬화에 사용할 직렬화기</param>
    public SessionAuthGate(
        IAuthTokenValidator validator, PlayerLevelSystem levelSystem, GameEventSink sink, BinaryPacketSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(levelSystem);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(serializer);

        _validator = validator;
        _levelSystem = levelSystem;
        _sink = sink;
        _serializer = serializer;
    }

    /// <summary>
    /// 수신된 패킷 1개를 처리합니다. <see cref="AuthTokenPacket"/>이 아니거나 이미 인증된 세션이면
    /// 조용히 무시합니다.
    /// </summary>
    /// <param name="session">패킷을 보낸 세션</param>
    /// <param name="data">헤더(4B)를 포함한 완전한 패킷 버퍼. 콜백 동안만 유효</param>
    /// <returns>이번 호출로 세션이 새로 인증에 성공했으면 <c>true</c>, 그 외(무관한 패킷·이미 인증됨·
    /// 검증 실패)에는 <c>false</c></returns>
    public async ValueTask<bool> HandleAsync(ISession session, ReadOnlyMemory<byte> data)
    {
        if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
            return false;
        if (packetId != AuthTokenPacket.Id)
            return false; // 이 사이클에서 클라이언트가 보낼 수 있는 다른 패킷은 없다 — 조용히 무시.
        if (session.State == SessionState.Authenticated)
            return false; // 중복 토큰 — 이미 결합된 Player를 다시 만들지 않는다(멱등).

        AuthTokenClaims claims;
        try
        {
            AuthTokenPacket packet = _serializer.Deserialize<AuthTokenPacket>(data.Span);
            if (!_validator.TryValidate(packet.Token, out claims))
            {
                await session.SendAsync(new AuthTokenAckPacket { Success = false });
                return false;
            }
        }
        catch
        {
            // 파싱 실패(손상된 패킷)도 검증 실패와 동일하게 취급 — 연결을 끊지 않고 실패로 응답한다.
            await session.SendAsync(new AuthTokenAckPacket { Success = false });
            return false;
        }

        // 세션ID를 함께 섞는 이유: accountId만으로 instanceId를 만들면 같은 계정이 두 세션으로
        // 동시 접속할 때 RaidRewardApplier의 InstanceId 키가 충돌해 보상이 엉뚱한 세션으로 갈 수
        // 있다. 세션마다 고유한 SessionId를 섞어 계정 추적성은 유지하면서 충돌을 원천 차단한다.
        var instanceId = $"player-{claims.AccountId}-{session.SessionId:N}";
        var player = PlayerFactory.Create(instanceId, claims.AccountId, level: 1, _levelSystem);
        session.Context = player;
        session.TransitionTo(SessionState.Authenticated);
        _sink.RecordPlayerConnected(player.InstanceId, player.Level);

        await session.SendAsync(new AuthTokenAckPacket { Success = true });
        return true;
    }
}
