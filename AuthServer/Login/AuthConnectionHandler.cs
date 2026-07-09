using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace AuthServer.Login;

/// <summary>
/// <see cref="IServerListener.OnReceived"/>에 배선되는 AuthServer의 유일한 수신 핸들러입니다.
/// <see cref="LoginRequestPacket"/>(Id=10)만 처리하고 나머지 패킷은 무시하며, 결과를
/// <see cref="LoginResponsePacket"/>으로 응답합니다. <c>Program.cs</c>와 E2E 테스트가 동일
/// 인스턴스를 배선해 배선 로직이 한 곳에서만 정의되도록 합니다.
/// </summary>
/// <remarks>
/// <b>[성능 및 동시성 제약 조건]</b>
/// <list type="bullet">
/// <item><description><b>Thread Safety:</b> Thread-safe. <see cref="ServerLib.Interface.IServerListener.OnReceived"/>는
/// 여러 세션에 대해 동시에 호출될 수 있으며, 이 클래스는 주입된 <see cref="LoginService"/>
/// (Thread-safe)만 참조하고 자체 가변 상태가 없습니다.</description></item>
/// <item><description><b>Memory Allocation:</b> <see cref="BinaryPacketSerializer.Deserialize{T}"/>가
/// <see cref="LoginRequestPacket"/> 인스턴스와 Username/Password string 2개를 힙에 할당합니다.</description></item>
/// <item><description><b>Blocking:</b> <b>고성능 네트워크 IO 스레드 풀에서 직접 호출됩니다.</b>
/// 내부적으로 <see cref="LoginService.AuthenticateAsync"/>를 거치며, 여기 포함된 PBKDF2 비밀번호
/// 검증은 동기 CPU 블로킹(수십 ms급)입니다 — 로그인은 저빈도 경로이므로 이번 사이클에서는 허용하되,
/// 처리량이 커지면 오프로드를 고려해야 합니다(§AuthServer 위험 항목 참고).</description></item>
/// </list>
/// </remarks>
public sealed class AuthConnectionHandler
{
    private readonly LoginService _login;
    private readonly BinaryPacketSerializer _serializer;

    /// <summary>로그인 서비스와 패킷 직렬화기를 주입하여 핸들러를 생성합니다.</summary>
    /// <param name="login">자격증명 검증·토큰 발급을 수행할 로그인 서비스입니다.</param>
    /// <param name="serializer">패킷 (역)직렬화에 사용할 직렬화기입니다.</param>
    public AuthConnectionHandler(LoginService login, BinaryPacketSerializer serializer)
    {
        _login = login;
        _serializer = serializer;
    }

    /// <summary>
    /// 수신된 패킷 1개를 처리합니다. <see cref="LoginRequestPacket"/>이 아니면 조용히 무시합니다.
    /// </summary>
    /// <param name="session">패킷을 보낸 클라이언트 세션입니다.</param>
    /// <param name="data">헤더(4B)를 포함한 완전한 패킷 버퍼입니다. 콜백 동안만 유효합니다.</param>
    public async ValueTask OnReceived(ISession session, ReadOnlyMemory<byte> data)
    {
        try
        {
            if (!PacketPool.TryParseHeader(data.Span, out ushort packetId, out _))
                return;
            if (packetId != LoginRequestPacket.Id)
                return; // 로그인 이외 패킷은 이 사이클의 책임 밖 — 조용히 무시(연결 유지)

            LoginRequestPacket request = _serializer.Deserialize<LoginRequestPacket>(data.Span);
            LoginResult result = await _login.AuthenticateAsync(request.Username, request.Password);

            await session.SendAsync(new LoginResponsePacket { Success = result.Success, Token = result.Token });
        }
        catch
        {
            // 파싱/처리 중 예외가 나도 연결을 끊지 않고 실패 응답으로 정리한다.
            // 예외를 그대로 던지면 OnClientError로 이어져 연결이 강제 종료된다.
            await session.SendAsync(new LoginResponsePacket { Success = false, Token = string.Empty });
        }
    }
}
