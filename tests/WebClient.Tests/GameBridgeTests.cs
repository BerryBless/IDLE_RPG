using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Stats;
using GameServer.Systems;
using ServerLib;
using ServerLib.Core.Auth;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;
using WebClient;

namespace WebClient.Tests;

/// <summary>
/// GameBridge 통합 테스트: 실제 루프백 게임 리스너 + 프로덕션 <see cref="SessionAuthGate"/>를
/// 조합해(FullStack.Tests 픽스처 패턴) join→토큰 발급→TCP 인증→브로드캐스트 JSON 번역→대칭 종료의
/// 전 수명주기를 검증한다. 브라우저는 <see cref="FakeBrowserChannel"/>이 대신한다(Kestrel 불필요).
/// </summary>
public sealed class GameBridgeTests : IDisposable
{
    private static readonly byte[] SharedSecret = "webclient-bridge-test-hmac-secret-32b"u8.ToArray();
    private static readonly BinaryPacketSerializer Serializer = new();
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private readonly CancellationTokenSource _cts = new();
    private GameEventSink? _sink;
    private IServerListener? _listener;
    private ISessionRegistry _registry = null!;
    private int _gamePort;

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>실제 게임 리스너(토큰 게이트만, 레이드 생략 — FullStack 픽스처와 동일 구성)를 기동한다.</summary>
    /// <param name="gateSecret">인증 게이트가 쓸 비밀키(불일치 시나리오 테스트용 재정의 가능)</param>
    private void StartGameListener(byte[]? gateSecret = null)
    {
        var levelSystem = PlayerLevelSystem.CreateDefault();
        _sink = new GameEventSink(new StringWriter(), new GameMetrics($"WebClientBridge.{Guid.NewGuid()}"));
        var authGate = new SessionAuthGate(
            new HmacAuthTokenCodec(gateSecret ?? SharedSecret), levelSystem, _sink, Serializer);

        _registry = ServerNet.CreateSessionRegistry();
        _listener = ServerNet.CreateListener(_registry);
        _listener.OnReceived = async (session, data) => { _ = await authGate.HandleAsync(session, data); };

        _gamePort = GetFreePort();
        _listener.Start(_gamePort, IPAddress.Loopback);
    }

    /// <summary>발급기·디렉터리·브리지를 프로덕션(Program.cs)과 동일 순서로 조립한다.</summary>
    private (GameBridge Bridge, GuestDirectory Directory) CreateBridge()
    {
        var directory = new GuestDirectory();
        var issuer = new GuestTokenIssuer(new HmacAuthTokenCodec(SharedSecret), directory, TimeSpan.FromMinutes(10));
        return (new GameBridge(issuer, directory, "127.0.0.1", _gamePort), directory);
    }

    /// <summary>패킷을 레지스트리 브로드캐스트용 프레임 바이트로 직렬화한다(RaidBroadcaster와 동일 경로).</summary>
    private static async Task BroadcastAsync<T>(ISessionRegistry registry, T packet) where T : IPacket
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(PacketPool.HeaderSize + packet.GetBodySize());
        try
        {
            int written = Serializer.Serialize(packet, buffer);
            await registry.BroadcastAsync(buffer.AsMemory(0, written));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "조건이 제한 시간 내에 충족되지 않았습니다.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task join하면_joined와_인증성공이_중계된다()
    {
        StartGameListener();
        var (bridge, _) = CreateBridge();
        var browser = new FakeBrowserChannel();

        Task run = bridge.RunAsync(browser, _cts.Token);
        browser.EnqueueFromBrowser("""{"type":"join","nickname":"웹용사"}""");

        JsonElement joined = await browser.WaitForAsync("joined", WaitTimeout);
        Assert.Equal("웹용사", joined.GetProperty("nickname").GetString());
        Assert.True(joined.GetProperty("accountId").GetInt32() >= GuestTokenIssuer.FirstGuestAccountId);

        JsonElement auth = await browser.WaitForAsync("auth", WaitTimeout);
        Assert.True(auth.GetProperty("success").GetBoolean());

        // 실제 서버 세션이 인증 상태로 붙어 있어야 한다.
        await WaitUntilAsync(() => _listener!.ActiveSessionCount == 1, WaitTimeout);

        browser.CloseFromBrowser();
        await run.WaitAsync(WaitTimeout);
        Assert.True(browser.Closed);
    }

    [Fact]
    public async Task 보스HP_브로드캐스트가_JSON으로_번역된다()
    {
        StartGameListener();
        var (bridge, _) = CreateBridge();
        var browser = new FakeBrowserChannel();
        Task run = bridge.RunAsync(browser, _cts.Token);
        browser.EnqueueFromBrowser("""{"type":"join","nickname":"딜러"}""");
        await browser.WaitForAsync("auth", WaitTimeout);
        await WaitUntilAsync(() => _listener!.ActiveSessionCount == 1, WaitTimeout);

        await BroadcastAsync(_registry, new MobHpPacket { Hp = 4_200_000, MaxHp = 5_000_000, Generation = 2 });

        JsonElement hp = await browser.WaitForAsync("bossHp", WaitTimeout);
        Assert.Equal(4_200_000, hp.GetProperty("hp").GetInt64());
        Assert.Equal(5_000_000, hp.GetProperty("maxHp").GetInt64());
        Assert.Equal(2, hp.GetProperty("generation").GetInt32());

        browser.CloseFromBrowser();
        await run.WaitAsync(WaitTimeout);
    }

    [Fact]
    public async Task 처치_브로드캐스트의_MVP는_내_닉네임으로_역매핑된다()
    {
        StartGameListener();
        var (bridge, _) = CreateBridge();
        var browser = new FakeBrowserChannel();
        Task run = bridge.RunAsync(browser, _cts.Token);
        browser.EnqueueFromBrowser("""{"type":"join","nickname":"엠브이피"}""");
        JsonElement joined = await browser.WaitForAsync("joined", WaitTimeout);
        int accountId = joined.GetProperty("accountId").GetInt32();
        await browser.WaitForAsync("auth", WaitTimeout);
        await WaitUntilAsync(() => _listener!.ActiveSessionCount == 1, WaitTimeout);

        // SessionAuthGate가 만드는 instanceId 포맷 그대로 MVP를 흉내낸다.
        await BroadcastAsync(_registry, new MobDeathPacket
        {
            Generation = 5,
            TopDamage = 123_456,
            MvpName = $"player-{accountId}-{Guid.NewGuid():N}",
        });

        JsonElement death = await browser.WaitForAsync("bossDeath", WaitTimeout);
        Assert.Equal("엠브이피", death.GetProperty("mvpName").GetString());
        Assert.True(death.GetProperty("mvpIsMe").GetBoolean());
        Assert.Equal(123_456, death.GetProperty("topDamage").GetInt64());

        browser.CloseFromBrowser();
        await run.WaitAsync(WaitTimeout);
    }

    [Fact]
    public async Task 모르는_MVP는_원문_노출에_mvpIsMe_false다()
    {
        StartGameListener();
        var (bridge, _) = CreateBridge();
        var browser = new FakeBrowserChannel();
        Task run = bridge.RunAsync(browser, _cts.Token);
        browser.EnqueueFromBrowser("""{"type":"join"}""");
        await browser.WaitForAsync("auth", WaitTimeout);
        await WaitUntilAsync(() => _listener!.ActiveSessionCount == 1, WaitTimeout);

        await BroadcastAsync(_registry, new MobDeathPacket
        {
            Generation = 1,
            TopDamage = 10,
            MvpName = "player-42-someoneelse", // 이 프로세스가 발급하지 않은 계정
        });

        JsonElement death = await browser.WaitForAsync("bossDeath", WaitTimeout);
        Assert.Equal("player-42-someoneelse", death.GetProperty("mvpName").GetString());
        Assert.False(death.GetProperty("mvpIsMe").GetBoolean());

        browser.CloseFromBrowser();
        await run.WaitAsync(WaitTimeout);
    }

    [Fact]
    public async Task 브라우저가_닫히면_서버_세션도_정리된다()
    {
        StartGameListener();
        var (bridge, directory) = CreateBridge();
        var browser = new FakeBrowserChannel();
        Task run = bridge.RunAsync(browser, _cts.Token);
        browser.EnqueueFromBrowser("""{"type":"join","nickname":"곧퇴장"}""");
        JsonElement joined = await browser.WaitForAsync("joined", WaitTimeout);
        int accountId = joined.GetProperty("accountId").GetInt32();
        await browser.WaitForAsync("auth", WaitTimeout);
        await WaitUntilAsync(() => _listener!.ActiveSessionCount == 1, WaitTimeout);

        browser.CloseFromBrowser();
        await run.WaitAsync(WaitTimeout);

        // WS 종료 → 브리지 종료 → TCP DisposeAsync → 서버 세션 0 + 디렉터리 해제까지 대칭 정리.
        await WaitUntilAsync(() => _listener!.ActiveSessionCount == 0, WaitTimeout);
        Assert.False(directory.TryResolveMvp($"player-{accountId}-x", out _, out _));
    }

    [Fact]
    public async Task 서버가_끊기면_브라우저에_disconnected가_통지된다()
    {
        StartGameListener();
        var (bridge, _) = CreateBridge();
        var browser = new FakeBrowserChannel();
        Task run = bridge.RunAsync(browser, _cts.Token);
        browser.EnqueueFromBrowser("""{"type":"join","nickname":"버림받음"}""");
        await browser.WaitForAsync("auth", WaitTimeout);
        await WaitUntilAsync(() => _listener!.ActiveSessionCount == 1, WaitTimeout);

        _listener!.Stop(); // 서버 강제 종료 — 활성 세션이 모두 닫힌다.

        JsonElement status = await browser.WaitForAsync("status", TimeSpan.FromSeconds(10));
        // "connecting"/"fighting"은 이미 소비됐고, Stop 이후 첫 status는 disconnected여야 한다.
        while (status.GetProperty("state").GetString() != "disconnected")
            status = await browser.WaitForAsync("status", TimeSpan.FromSeconds(10));

        await run.WaitAsync(WaitTimeout);
        Assert.True(browser.Closed);
    }

    [Fact]
    public async Task 게이트_비밀키가_다르면_인증실패가_중계된다()
    {
        StartGameListener(gateSecret: "mismatched-secret-on-the-game-side-32b!"u8.ToArray());
        var (bridge, _) = CreateBridge();
        var browser = new FakeBrowserChannel();
        Task run = bridge.RunAsync(browser, _cts.Token);
        browser.EnqueueFromBrowser("""{"type":"join","nickname":"거부됨"}""");

        JsonElement auth = await browser.WaitForAsync("auth", WaitTimeout);
        Assert.False(auth.GetProperty("success").GetBoolean());
        await browser.WaitForAsync("error", WaitTimeout);

        await run.WaitAsync(WaitTimeout);
        Assert.True(browser.Closed);
    }

    [Fact]
    public async Task 첫_메시지가_join이_아니면_오류_후_종료한다()
    {
        StartGameListener();
        var (bridge, _) = CreateBridge();
        var browser = new FakeBrowserChannel();
        Task run = bridge.RunAsync(browser, _cts.Token);
        browser.EnqueueFromBrowser("""{"type":"attack"}""");

        await browser.WaitForAsync("error", WaitTimeout);
        await run.WaitAsync(WaitTimeout);
        Assert.True(browser.Closed);
        Assert.Equal(0, _listener!.ActiveSessionCount); // TCP 접속 자체를 만들지 않는다.
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch (Exception) { }
        try { _sink?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch (Exception) { }
        _cts.Dispose();
    }
}
