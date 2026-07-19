using System.Net;
using System.Runtime.InteropServices;
using AuthServer.Accounts;
using AuthServer.Configuration;
using AuthServer.Login;
using AuthServer.Security;
using AuthServer.Seeding;
using MongoDB.Driver;
using ServerLib;
using ServerLib.Core.Auth;
using ServerLib.Core.Serialization;
using ServerLib.Interface;

// AuthServer 진입점. LoginRequestPacket(Id=10)을 받아 MongoDB 계정을 검증하고 HMAC 토큰을 발급하는
// 별도 프로세스다(설계: plan/login_mongo_0709.md). GameServer와 분리한 이유는 인증 책임을 게임
// 로직과 격리하고, 다음 사이클에서 GameServer가 이 토큰을 DB 조회 없이(무상태) 검증만 하면 되도록
// 하기 위함.
//
// 이번 사이클 범위: 로그인·토큰 발급 + 3000 더미 계정 시딩까지. GameServer가 AuthTokenPacket으로
// 이 토큰을 실제로 검증해 인증 게이트를 통과시키는 배선은 다음 사이클 과제다(Main.cs는 아직 미변경).

Console.WriteLine(
    $"[초기화] 설정 로딩 완료 - bind={AuthServerConfig.BindAddress}:{AuthServerConfig.Port}, " +
    $"mongo={AuthServerConfig.MongoConnectionString}/{AuthServerConfig.MongoDatabaseName}");

if (AuthServerConfig.IsUsingDevHmacSecret)
{
    Console.WriteLine(
        "[경고] IDLERPG_AUTH_HMAC_SECRET 환경 변수가 없어 개발용 기본 비밀키를 사용합니다. " +
        "운영 배포 전 반드시 재정의하고, 다음 사이클에서 GameServer와 동일 값을 공유해야 합니다.");
}

var mongoRepo = new MongoAccountRepository(AuthServerConfig.MongoConnectionString, AuthServerConfig.MongoDatabaseName);
var hasher = new Pbkdf2PasswordHasher();
Console.WriteLine("[초기화] MongoAccountRepository + Pbkdf2PasswordHasher 생성 완료(아직 연결 시도는 하지 않음).");

// --seed: 실 MongoDB에 결정적 더미 계정을 채우고 종료한다. 테스트(AccountCorrectnessTests)가
// 인메모리 페이크에 대해 검증한 것과 동일한 AccountSeeder 로직을 재사용하므로 사람이 실제로
// user0000/Pass!0000 패턴 자격증명으로 로그인해 볼 수 있다.
if (args.Contains("--seed"))
{
    await RunSeedAsync(mongoRepo, hasher, force: args.Contains("--force"));
    return;
}

var codec = new HmacAuthTokenCodec(AuthServerConfig.HmacSecret);
var login = new LoginService(mongoRepo, hasher, codec, AuthServerConfig.TokenLifetime);
var handler = new AuthConnectionHandler(login, new BinaryPacketSerializer());
Console.WriteLine($"[초기화] 토큰 코덱 + 로그인 서비스 + 연결 핸들러 생성 완료(토큰 유효기간 {AuthServerConfig.TokenLifetime.TotalMinutes:0}분).");

// CancellationTokenSource: Ctrl+C(SIGINT) 기본 동작인 즉시 프로세스 종료 대신 협조적 취소로
// 바꾼다 — GameServer/Main.cs와 동일한 종료 패턴.
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
// PosixSignalRegistration(SIGTERM): docker compose down/stop이 보내는 신호는 SIGINT가 아니라
// SIGTERM이라 위 CancelKeyPress만으로는 잡히지 않는다 — GameServer/Main.cs와 동일 이유로 등록
// (Windows 로컬 dotnet run에는 SIGTERM 자체가 없어 영향 없음).
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    cts.Cancel();
});

IServerListener listener = ServerNet.CreateListener();
Console.WriteLine("[초기화] 리스너 생성 완료(아직 포트를 열지는 않음).");
// OnReceived는 Start() 호출 전에 배선해야 한다(이후 설정 시 InvalidOperationException).
listener.OnReceived = handler.OnReceived;
// SessionSendTimeout: 응답을 받지 않는 정지된 클라이언트 하나가 송신을 무한 대기시키지 않도록
// 유한값으로 설정(GameServer의 공유 보스 co-op 코드리뷰에서 발견된 동일 위험 사전 반영).
listener.SessionSendTimeout = TimeSpan.FromSeconds(2);
Console.WriteLine("[초기화] OnReceived 핸들러 배선 + SessionSendTimeout(2s) 설정 완료.");

// AuthServerConfig.BindAddress 기본값은 루프백: LoginRequestPacket.Password는 평문 전송이라(패킷
// 주석 참고) TLS 없이 외부에 노출하면 위험하다. TLS가 도입되기 전까지 GameServer와 동일하게 루프백을
// 기본값으로 유지하고, Docker 등 신뢰 경계가 다른 환경에서는 IDLERPG_AUTH_BIND로 명시적으로만 넓힌다.
listener.Start(AuthServerConfig.Port, AuthServerConfig.BindAddress);
Console.WriteLine($"[가동] AuthServer 리스너 시작 -> {AuthServerConfig.BindAddress}:{AuthServerConfig.Port} (로그인 요청 수락 시작)");
Console.WriteLine("[가동] AuthServer 초기화 완료 - 모든 컴포넌트가 정상 기동되었습니다.");

try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C로 정상 종료 진입 — 아래에서 리스너를 멈춘다.
}

listener.Stop();

static async Task RunSeedAsync(MongoAccountRepository repo, Pbkdf2PasswordHasher hasher, bool force)
{
    await repo.EnsureIndexesAsync();

    if (force)
    {
        Console.WriteLine("--force 지정됨: 기존 accounts 컬렉션을 비우고 다시 시딩합니다.");
        await repo.DropAllAsync();
    }

    const int SeedCount = 3000;
    try
    {
        await AccountSeeder.SeedAsync(repo, hasher, SeedCount);
        Console.WriteLine(
            $"{SeedCount}개 더미 계정 시딩 완료 " +
            $"({AccountSeeder.UsernameFor(0)}..{AccountSeeder.UsernameFor(SeedCount - 1)}, " +
            $"비밀번호는 {AccountSeeder.PasswordFor(0)} 패턴).");
    }
    catch (MongoBulkWriteException ex)
    {
        Console.WriteLine(
            $"일부 계정이 이미 존재하여 시딩이 중단되었습니다({ex.Message}). " +
            "재시딩하려면 'dotnet run --project AuthServer -- --seed --force'를 사용하세요.");
    }
}
