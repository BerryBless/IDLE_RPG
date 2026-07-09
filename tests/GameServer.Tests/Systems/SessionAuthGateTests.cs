using System.Net;
using GameServer.Entities;
using GameServer.Systems;
using ServerLib.Core;
using ServerLib.Core.Auth;
using ServerLib.Core.Memory;
using ServerLib.Core.Serialization;
using ServerLib.Core.Serialization.Packets;
using ServerLib.Interface;

namespace GameServer.Tests.Systems;

/// <summary>
/// <see cref="SessionAuthGate"/>가 <see cref="AuthTokenPacket"/>을 검증해 실제 <see cref="Player"/>를
/// 결합하는 흐름을 소켓 없이 단위 검증한다. <see cref="SessionRaidRunnerEdgeCaseTests"/>와 동일한
/// <c>FakeSession</c> 패턴을 따르되, 응답 패킷 캡처를 위해 <c>SendAsync</c>가 마지막으로 보낸
/// 바이트를 기록하도록 확장한다.
/// </summary>
public class SessionAuthGateTests
{
    private static readonly byte[] TestSecret = "session-auth-gate-test-secret"u8.ToArray();
    private static readonly BinaryPacketSerializer Serializer = new();

    private sealed class FakeSession : ISession
    {
        public Guid SessionId { get; init; } = Guid.NewGuid();
        public EndPoint? RemoteEndPoint => null;
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset LastReceivedAt => ConnectedAt;
        public DateTimeOffset LastProgressAt => ConnectedAt;
        public SessionState State { get; private set; } = SessionState.Connected;
        public bool TransitionTo(SessionState newState) { State = newState; return true; }
        public object? Context { get; set; }
        public Func<ReadOnlyMemory<byte>, ValueTask>? OnReceived { get; set; }
        public Func<ValueTask>? OnDisconnected { get; set; }
        public Func<Exception, ValueTask>? OnReceiveError { get; set; }

        // LastSent: SendAsync가 호출될 때마다 보낸 바이트를 복사해 보관 — 테스트가 Ack 패킷 내용을 검증할 수 있게 함.
        public byte[]? LastSent { get; private set; }

        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            LastSent = data.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static byte[] BuildAuthTokenPacketBytes(string token)
    {
        var packet = new AuthTokenPacket { Token = token };
        var buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        Serializer.Serialize(packet, buf);
        return buf;
    }

    private static byte[] BuildEchoPacketBytes(string message)
    {
        var packet = new EchoPacket { Message = message };
        var buf = new byte[PacketPool.HeaderSize + packet.GetBodySize()];
        Serializer.Serialize(packet, buf);
        return buf;
    }

    private static SessionAuthGate CreateGate(GameEventSink sink, HmacAuthTokenCodec codec) =>
        new(codec, PlayerLevelSystem.CreateDefault(), sink, Serializer);

    [Fact]
    public async Task HandleAsync_ValidToken_BindsRealPlayerAndReturnsTrue()
    {
        var metrics = new GameMetrics($"Test.SessionAuthGate.{Guid.NewGuid()}");
        await using var sink = new GameEventSink(new StringWriter(), metrics);
        var codec = new HmacAuthTokenCodec(TestSecret);
        var gate = CreateGate(sink, codec);
        var session = new FakeSession();
        string token = codec.Issue(accountId: 42, username: "alice", DateTimeOffset.UtcNow.AddMinutes(10));

        bool result = await gate.HandleAsync(session, BuildAuthTokenPacketBytes(token));

        Assert.True(result);
        Assert.Equal(SessionState.Authenticated, session.State);
        var player = Assert.IsType<Player>(session.Context);
        Assert.Equal(42, player.AccountId);
        Assert.True(player.IsAlive);

        Assert.NotNull(session.LastSent);
        var ack = Serializer.Deserialize<AuthTokenAckPacket>(session.LastSent!);
        Assert.True(ack.Success);
    }

    [Fact]
    public async Task HandleAsync_GarbageToken_SendsFailureAckAndReturnsFalse()
    {
        var metrics = new GameMetrics($"Test.SessionAuthGate.{Guid.NewGuid()}");
        await using var sink = new GameEventSink(new StringWriter(), metrics);
        var codec = new HmacAuthTokenCodec(TestSecret);
        var gate = CreateGate(sink, codec);
        var session = new FakeSession();

        bool result = await gate.HandleAsync(session, BuildAuthTokenPacketBytes("not-a-valid-token"));

        Assert.False(result);
        Assert.Equal(SessionState.Connected, session.State);
        Assert.Null(session.Context);

        var ack = Serializer.Deserialize<AuthTokenAckPacket>(session.LastSent!);
        Assert.False(ack.Success);
    }

    [Fact]
    public async Task HandleAsync_ExpiredToken_SendsFailureAckAndReturnsFalse()
    {
        var metrics = new GameMetrics($"Test.SessionAuthGate.{Guid.NewGuid()}");
        await using var sink = new GameEventSink(new StringWriter(), metrics);
        var codec = new HmacAuthTokenCodec(TestSecret);
        var gate = CreateGate(sink, codec);
        var session = new FakeSession();
        string expired = codec.Issue(1, "bob", DateTimeOffset.UtcNow.AddSeconds(-1));

        bool result = await gate.HandleAsync(session, BuildAuthTokenPacketBytes(expired));

        Assert.False(result);
        Assert.Null(session.Context);
        var ack = Serializer.Deserialize<AuthTokenAckPacket>(session.LastSent!);
        Assert.False(ack.Success);
    }

    [Fact]
    public async Task HandleAsync_TokenSignedWithDifferentSecret_SendsFailureAckAndReturnsFalse()
    {
        var metrics = new GameMetrics($"Test.SessionAuthGate.{Guid.NewGuid()}");
        await using var sink = new GameEventSink(new StringWriter(), metrics);
        var issuer = new HmacAuthTokenCodec("a-completely-different-secret"u8.ToArray());
        var validatorCodec = new HmacAuthTokenCodec(TestSecret);
        var gate = CreateGate(sink, validatorCodec);
        var session = new FakeSession();
        string token = issuer.Issue(1, "carol", DateTimeOffset.UtcNow.AddMinutes(10));

        bool result = await gate.HandleAsync(session, BuildAuthTokenPacketBytes(token));

        Assert.False(result);
        Assert.Null(session.Context);
    }

    [Fact]
    public async Task HandleAsync_NonAuthTokenPacket_IgnoredReturnsFalse()
    {
        var metrics = new GameMetrics($"Test.SessionAuthGate.{Guid.NewGuid()}");
        await using var sink = new GameEventSink(new StringWriter(), metrics);
        var codec = new HmacAuthTokenCodec(TestSecret);
        var gate = CreateGate(sink, codec);
        var session = new FakeSession();

        bool result = await gate.HandleAsync(session, BuildEchoPacketBytes("hello"));

        Assert.False(result);
        Assert.Null(session.Context);
        Assert.Equal(SessionState.Connected, session.State);
        Assert.Null(session.LastSent); // 무관한 패킷은 응답조차 보내지 않는다(조용히 무시).
    }

    [Fact]
    public async Task HandleAsync_SecondValidTokenAfterAlreadyAuthenticated_IgnoredReturnsFalse()
    {
        var metrics = new GameMetrics($"Test.SessionAuthGate.{Guid.NewGuid()}");
        await using var sink = new GameEventSink(new StringWriter(), metrics);
        var codec = new HmacAuthTokenCodec(TestSecret);
        var gate = CreateGate(sink, codec);
        var session = new FakeSession();
        string token = codec.Issue(7, "dave", DateTimeOffset.UtcNow.AddMinutes(10));

        bool first = await gate.HandleAsync(session, BuildAuthTokenPacketBytes(token));
        var playerAfterFirst = session.Context;

        bool second = await gate.HandleAsync(session, BuildAuthTokenPacketBytes(token));

        Assert.True(first);
        Assert.False(second);
        // 중복 토큰이 Player를 다시 만들지 않았다는 것 — 동일 참조가 그대로 유지됨.
        Assert.Same(playerAfterFirst, session.Context);
    }

    [Fact]
    public async Task HandleAsync_SameAccountDifferentSessions_ProducesDistinctInstanceIds()
    {
        // 동시 다중 로그인 시 RaidRewardApplier의 InstanceId 키 충돌(보상 오배송)을 막기 위한
        // instanceId 유일성(accountId+sessionId 결합) 보증을 검증한다.
        var metrics = new GameMetrics($"Test.SessionAuthGate.{Guid.NewGuid()}");
        await using var sink = new GameEventSink(new StringWriter(), metrics);
        var codec = new HmacAuthTokenCodec(TestSecret);
        var gate = CreateGate(sink, codec);

        var sessionA = new FakeSession();
        var sessionB = new FakeSession();
        string tokenA = codec.Issue(99, "shared-account", DateTimeOffset.UtcNow.AddMinutes(10));
        string tokenB = codec.Issue(99, "shared-account", DateTimeOffset.UtcNow.AddMinutes(10));

        await gate.HandleAsync(sessionA, BuildAuthTokenPacketBytes(tokenA));
        await gate.HandleAsync(sessionB, BuildAuthTokenPacketBytes(tokenB));

        var playerA = Assert.IsType<Player>(sessionA.Context);
        var playerB = Assert.IsType<Player>(sessionB.Context);
        Assert.Equal(99, playerA.AccountId);
        Assert.Equal(99, playerB.AccountId);
        Assert.NotEqual(playerA.InstanceId, playerB.InstanceId);
    }
}
