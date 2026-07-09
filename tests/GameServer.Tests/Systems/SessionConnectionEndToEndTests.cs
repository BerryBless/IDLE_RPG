using System.Net;
using System.Net.Sockets;
using GameServer.Entities;
using GameServer.Systems;
using ServerLib;
using ServerLib.Core;
using ServerLib.Interface;

namespace GameServer.Tests.Systems;

/// <summary>
/// <see cref="SessionPlayerBinder"/> 자체의 연결/해제 계약(<see cref="Player"/> 생성·부착, 해제
/// 시 정리)을 실제 루프백 TCP 소켓으로 검증합니다.
/// </summary>
/// <remarks>
/// <b>2026-07-09 토큰 게이트 도입 이후:</b> <c>Main.cs</c>는 더 이상 접속 즉시
/// <see cref="SessionPlayerBinder.OnConnected"/>를 호출하지 않습니다(<see cref="SessionAuthGate"/>가
/// 대체 — <c>plan/gameserver_auth_gate_0709.md</c> 참고). 이 테스트는 더 이상 "Main.cs 배선 재현"이
/// 아니라, <see cref="SessionPlayerBinder"/> 클래스 자체가 실소켓 위에서 여전히 올바르게 동작하는지
/// (다른 테스트들이 이 클래스를 픽스처 헬퍼로 계속 의존하므로)를 검증하는 단위 계약 테스트입니다.
/// <c>Main.cs</c>의 실제 신규 배선은 <c>SessionAuthGateEndToEndTests</c>가 검증합니다.
/// <br/><br/>
/// <c>Main.cs</c>는 top-level 문이라 테스트에서 직접 호출할 수 없습니다(<c>EchoEndToEndTests</c>와
/// 동일한 이유). 대신 <see cref="SessionPlayerBinder"/>(실제 프로덕션 클래스)를 실제
/// <see cref="ServerNet.CreateListener"/> 콜백에 그대로 연결해 그 계약을 재현합니다.
/// <para>
/// <b>[검증 전략]</b> <see cref="GameEventSink"/>의 NDJSON 파일 쓰기는 내부 채널을 거쳐 비동기로
/// 수행되므로(별도 소비자 태스크), 콜백 완료 직후 파일/StringWriter 내용을 읽으면 경쟁 상태가 된다
/// (기존 <c>GameEventSinkTests.RecordMonsterDefeated_IncrementsMetricAndWritesLine</c>도 같은 이유로
/// 라인 내용이 아닌 동기적인 <see cref="System.Diagnostics.Metrics.Counter{T}"/> 값만 단언한다).
/// 이 테스트도 동일한 원칙을 따른다: (1) <c>session.Context</c>에 부착된 <see cref="Player"/>의
/// <c>InstanceId</c>는 콜백이 반환되기 전에 동기적으로 읽으므로 안전하게 단언할 수 있고,
/// (2) NDJSON 기록 여부는 <see cref="System.Diagnostics.Metrics.MeterListener"/>로 카운터 증가만
/// 확인한다.
/// </para>
/// </remarks>
public class SessionConnectionEndToEndTests
{
    // TcpListener(Loopback, 0): OS가 미사용 임시 포트를 배정 → 병렬 테스트 간 포트 충돌 방지.
    // EchoEndToEndTests.GetFreePort()와 동일 패턴.
    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task Connect_ThenDisconnect_SameTempPlayerIdRoundTripsThroughSessionContext()
    {
        const int TimeoutMs = 5_000;

        // 병렬 실행 중인 다른 테스트와 계측기 이름이 겹치지 않도록 고유 이름을 쓴다
        // (GameMetricsTests/GameEventSinkTests와 동일 이유).
        var meterName = $"Test.SessionPlayerBinder.{Guid.NewGuid()}";
        var metrics = new GameMetrics(meterName);

        // StringWriter: 실제 파일 I/O 없이 NDJSON 출력 대상만 필요한 테스트에서 사용
        // (GameEventSinkTests와 동일 패턴). 이 테스트는 라인 내용을 읽지 않으므로 내용 검증에는
        // 쓰이지 않고, GameEventSink 생성자가 TextWriter를 요구하기 때문에 필요하다.
        await using var sink = new GameEventSink(new StringWriter(), metrics);

        var levelSystem = PlayerLevelSystem.CreateDefault();
        var binder = new SessionPlayerBinder(levelSystem, sink);

        int connectedCount = 0, disconnectedCount = 0;
        using var listener1 = new System.Diagnostics.Metrics.MeterListener();
        listener1.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == meterName &&
                (instrument.Name == "game.player.connected" || instrument.Name == "game.player.disconnected"))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener1.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "game.player.connected") connectedCount += (int)measurement;
            else if (instrument.Name == "game.player.disconnected") disconnectedCount += (int)measurement;
        });
        listener1.Start();

        // TaskCompletionSource<string>: IO 스레드(binder 콜백)에서 세션에 부착된 Player의
        // InstanceId를 테스트 스레드로 전달하는 신호기. RunContinuationsAsynchronously로
        // await 대기자가 IO 스레드에서 인라인 실행되지 않게 한다(EchoEndToEndTests와 동일 근거).
        var connectedId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnectedId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        int port = GetFreePort();
        IServerListener listener = ServerNet.CreateListener();

        // binder.OnConnected/OnDisconnected(실제 프로덕션 로직)를 그대로 호출한 뒤, 콜백이
        // 반환되기 전(Context가 아직 유효한 시점)에 InstanceId를 동기적으로 읽어 신호를 보낸다.
        listener.OnClientConnected = async session =>
        {
            await binder.OnConnected(session);
            connectedId.TrySetResult(session.GetContext<Player>()?.InstanceId ?? string.Empty);
        };
        listener.OnClientDisconnected = async session =>
        {
            // 소스 확인(SocketPipelineListener.cs): OnClientDisconnected는 session.DisposeAsync()
            // 이전에 호출되므로 Context는 binder.OnDisconnected 호출 시점에 아직 유효하다.
            string? idBeforeCleanup = session.GetContext<Player>()?.InstanceId;
            await binder.OnDisconnected(session);
            disconnectedId.TrySetResult(idBeforeCleanup ?? string.Empty);
        };
        listener.OnClientError = binder.OnError;

        listener.Start(port, IPAddress.Loopback);

        try
        {
            var client = ServerNet.CreateClient();
            await client.ConnectAsync("127.0.0.1", port);

            string idAtConnect = await connectedId.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));
            Assert.StartsWith("player-", idAtConnect);

            // 클라이언트 연결 해제 → 서버 쪽 OnClientDisconnected 발화(정상 동작 중 해제 —
            // 셧다운 시점 경쟁은 검증하지 않는다).
            await client.DisposeAsync();

            string idAtDisconnect = await disconnectedId.Task.WaitAsync(TimeSpan.FromMilliseconds(TimeoutMs));

            // 동일 id라는 것 자체가 session.Context가 두 콜백(OnConnected/OnDisconnected) 사이에서
            // 올바르게 왕복했다는 증거다 — 실제 TCP 연결 위에서.
            Assert.Equal(idAtConnect, idAtDisconnect);
            Assert.Equal(1, connectedCount);
            Assert.Equal(1, disconnectedCount);
        }
        finally
        {
            listener.Stop();
        }
    }
}
