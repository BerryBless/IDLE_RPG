# 웹 실시간 모니터링 대시보드

## 1. 배경 및 목적

GameServer의 라이브 상태(공유 레이드 보스 HP, 접속자 수, 세대, MVP 등)를 웹 브라우저에서 실시간으로
관전/운영하고 싶다는 요구에서 시작했다. 기존 프로젝트에는 HTTP/웹 표면이 전혀 없었고(모든 네트워킹은
`ServerLib` 기반 raw TCP), 모든 런타임 상태는 GameServer 프로세스 메모리 안에만 존재했다(외부 DB 없음).

브레인스토밍으로 확정한 3가지 결정:

1. **핵심 목적** = 실시간 서버 상태 대시보드(이벤트 로그 브라우저·메트릭 통합·제어판은 다음 사이클)
2. **호스팅** = 별도 모니터링 프로세스(GameServer 인프로세스 아님)
3. **데이터 다리(IPC)** = TCP 텔레메트리 피드(파일 스냅샷·DB 아님)

이 조합의 핵심 이득: 웹 의존성(ASP.NET Core)이 신규 `MonitorServer`에만 들어가고, GameServer/ServerLib의
"외부 의존성 0" 철학([serverlib_echo_import_0708.md](serverlib_echo_import_0708.md), `AuthServer.csproj`
주석의 "ServerLib에는 절대 반입하지 않고")을 깨지 않는다.

## 2. 설계 결정

| 항목 | 채택 | 대안 | 채택 사유 |
|------|------|------|-----------|
| 웹 서버 위치 | 별도 `MonitorServer` 프로세스 | GameServer 인프로세스 호스팅 | 웹 의존성을 GameServer/ServerLib에서 완전히 격리 |
| GameServer↔MonitorServer IPC | TCP 텔레메트리 피드(`ServerLib` 클라이언트) | 파일 스냅샷, Redis/DB | 기존 `ServerLib` 인프라 재사용, 신규 외부 의존성 0, 진짜 실시간(1초 이하 지연) |
| MonitorServer→브라우저 전송 | ASP.NET Core 미니멀 API + SSE | WebSocket, 폴링 | 단방향 서버→브라우저 푸시로 데이터 성격에 부합, WebSocket보다 구현이 단순 |
| 텔레메트리 스코프(v1) | 이미 스레드 안전한 집계 신호만(접속자 수·보스 HP/세대/MVP·리스너 통계) | 플레이어별 상세(레벨/골드/기여도) 포함 | 아래 §2.1 참고 — 플레이어별 상태는 크로스 스레드 읽기가 데이터 레이스 |

### 2.1 스코프 제외 근거(중요 불변식)

`Player` 변경(레벨/골드/기여도)은 그 세션을 소유한 제출 루프(`SessionRaidRunner.SubmitLoopAsync`)에서만
안전하게 읽고 쓸 수 있는 단일 소유자 상태다(`SessionRaidRunner.cs` 클래스 주석의 "보상 단일 소유
원칙" 참고). `RaidEncounter._contributions` 역시 액터 단일 스레드 소유 `Dictionary`다. 텔레메트리
퍼블리시 루프(1초 주기, 별도 스레드)가 이 값들을 직접 읽으면 데이터 레이스/tearing이 발생한다 —
그래서 v1은 **이미 브로드캐스트 목적으로 값 타입 스냅샷을 만들어 넘겨주는 `RaidStepBroadcast`**와
**이미 thread-safe로 문서화된 `ISessionRegistry`/`IServerListener` 속성**만 사용한다. 보스 HP를
`RaidEncounter._boss.FinalStats`에서 직접 읽는 것도 동일한 이유로 금지된다(액터 단일 소유,
`RaidEncounter.cs` 클래스 주석의 "⚠️ 불변식" 참고) — 반드시 `onStep` 콜백이 넘겨주는
`RaidStepBroadcast` 값으로만 얻는다.

## 3. 컴포넌트 구조

```
GameServer(프로세스 A, 포트 7777 게임 + 7779 텔레메트리)
├── RaidEncounter 액터 ──onStep(RaidStepBroadcast)──┬─▶ RaidBroadcaster (기존, 게임 클라용, 7777)
│                                                    └─▶ TelemetryPublisher (신규)
│                                                          │ 1초 PeriodicTimer + 용량1 DropOldest 채널
│                                                          │ + IServerListener(7777) 통계 샘플링
│                                                          ▼
│                                    텔레메트리 리스너(신규, 인증 없음, 루프백 전용, 포트 7779)
└──────────────────────────────────┬────────────────────────────────────────────────────
                                    │ TelemetrySnapshotPacket(Id=19), 1초 주기 브로드캐스트
                                    ▼
MonitorServer(프로세스 B, 신규)
├── TelemetryClientLoop — ServerLib 클라이언트로 7779 구독, 끊기면 자동 재접속
├── TelemetrySnapshotStore — volatile 참조 교체 기반 "최신 스냅샷 1개" 홀더(락 없음)
└── ASP.NET Core 미니멀 API(포트 8080, 환경변수로 재정의 가능)
      GET /       → DashboardHtml.Page (단일 HTML, 인라인 CSS/JS, 외부 CDN 없음)
      GET /events → text/event-stream, 1초 주기로 MonitorSnapshot을 camelCase JSON으로 푸시
                        │
                        ▼
                  운영자 브라우저 (EventSource('/events') 구독, HP 바·통계 타일 실시간 갱신)
```

의존 관계: `MonitorServer` → `ServerLib`만 참조한다(`GameServer`를 참조하지 않는다 —
`TelemetrySnapshotPacket`이 ServerLib에 있으므로 게임 도메인 전체를 끌어올 필요가 없다).
`GameServer`/`ServerLib`는 여전히 외부 NuGet 의존성이 0이다.

## 4. 핵심 API

### 4.1 GameServer 측 — onStep 팬아웃 (`SessionRaidRunner.Start`)

```csharp
Func<RaidStepBroadcast, CancellationToken, ValueTask> onStep = _telemetryPublisher is null
    ? _broadcaster.OnStepAsync
    : async (info, ct) =>
    {
        await _broadcaster.OnStepAsync(info, ct);   // 게임 클라이언트용 (기존)
        await _telemetryPublisher.OnStep(info, ct); // 모니터링용 (신규)
    };
_ = Task.Run(() => _raid.RunAsync(_sink, lifetimeToken, onStep), lifetimeToken);
```

두 콜백 모두 내부 채널에 `TryWrite`만 하고 즉시 반환하는 계약(각자 클래스 주석)이라, 합성해도
레이드 액터 루프가 실질적으로 지연되지 않는다.

### 4.2 GameServer 측 — 텔레메트리 퍼블리셔 (`TelemetryPublisher`)

```csharp
// "최신 값만 유지" 락-프리 메일박스: 용량 1 + DropOldest
private readonly Channel<RaidStepBroadcast> _bossLatestChannel = Channel.CreateBounded<RaidStepBroadcast>(
    new BoundedChannelOptions(1) { SingleWriter = true, SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

public ValueTask OnStep(RaidStepBroadcast info, CancellationToken ct)
{
    _bossLatestChannel.Writer.TryWrite(info); // non-blocking, 즉시 반환
    return ValueTask.CompletedTask;
}

public async Task PublishLoopAsync(CancellationToken lifetimeToken)
{
    using var timer = new PeriodicTimer(_publishInterval); // 기본 1초
    while (await timer.WaitForNextTickAsync(lifetimeToken))
    {
        while (_bossLatestChannel.Reader.TryRead(out var step)) _cachedBossStep = step;
        var packet = new TelemetrySnapshotPacket { /* _gameListener 통계 + _cachedBossStep */ };
        await BroadcastPacketAsync(packet, lifetimeToken); // telemetryRegistry.BroadcastAsync
    }
}
```

### 4.3 MonitorServer 측 — 재접속 루프 (`TelemetryClientLoop`)

```csharp
while (!lifetimeToken.IsCancellationRequested)
{
    await using IClientConnection client = ServerNet.CreateClient(); // 재접속마다 새 인스턴스 필수
    var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    client.OnReceived = data => { /* TelemetrySnapshotPacket 역직렬화 → store.Update(...) */ };
    client.OnDisconnected = () => { disconnected.TrySetResult(); return ValueTask.CompletedTask; };
    try { await client.ConnectAsync(host, port, lifetimeToken); await disconnected.Task.WaitAsync(lifetimeToken); }
    catch { /* 연결 실패 — 아래 공통 경로에서 재시도 */ }
    store.MarkDisconnected();
    await Task.Delay(reconnectDelay, lifetimeToken);
}
```

### 4.4 MonitorServer 측 — SSE 엔드포인트 (`Program.cs`)

```csharp
app.MapGet("/events", async (HttpContext ctx, CancellationToken requestAborted) =>
{
    ctx.Response.ContentType = "text/event-stream";
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, cts.Token);
    while (!linked.IsCancellationRequested)
    {
        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(store.Current, jsonOptions)}\n\n", linked.Token);
        await ctx.Response.Body.FlushAsync(linked.Token);
        await Task.Delay(TimeSpan.FromSeconds(1), linked.Token);
    }
});
```

## 5. 변경 파일 목록

### 신규
- `ServerLib/Core/Serialization/Packets/TelemetrySnapshotPacket.cs` — 텔레메트리 스냅샷 패킷(Id=19, 다음 빈 값)
- `GameServer/Systems/TelemetryPublisher.cs` — onStep 두 번째 컨슈머 + 1초 퍼블리시 루프
- `MonitorServer/MonitorServer.csproj` — `Microsoft.NET.Sdk.Web` 기반 신규 exe(`ServerLib`만 참조)
- `MonitorServer/Program.cs` — 재접속 루프 기동 + ASP.NET Core 미니멀 API(`/`, `/events`)
- `MonitorServer/TelemetryClientLoop.cs` — GameServer 텔레메트리 리스너 재접속 구독 루프
- `MonitorServer/TelemetrySnapshotStore.cs` — volatile 참조 교체 기반 최신 스냅샷 홀더
- `MonitorServer/MonitorSnapshot.cs` — 브라우저로 내보내는 불변 JSON DTO(연결 상태 포함)
- `MonitorServer/DashboardHtml.cs` — 단일 페이지 대시보드(인라인 CSS/JS, HP 바 + 통계 타일)
- `tests/GameServer.Tests/Systems/TelemetrySnapshotPacketRoundTripTests.cs`
- `tests/GameServer.Tests/Systems/TelemetryPublisherTests.cs`
- `tests/GameServer.Tests/Systems/TelemetryPublisherEndToEndTests.cs` — 실소켓 2연결 byte-identical 검증

### 수정
- `GameServer/Systems/SessionRaidRunner.cs` — 생성자에 선택적 `TelemetryPublisher?` 추가, `Start()`에서 onStep 팬아웃 + 퍼블리시 루프 기동
- `GameServer/Main.cs` — 텔레메트리 전용 세션 레지스트리·리스너(포트 7779, 루프백, 인증 없음) 배선, `TelemetryPublisher` 생성·주입, 종료 시 `Stop()`
- `IDLE_RPG.sln` — `MonitorServer` 프로젝트 등록

## 6. 빌드 검증

```bash
dotnet build IDLE_RPG.sln    # 0 오류
dotnet test IDLE_RPG.sln     # GameServer.Tests 176/176 통과(신규 6건 포함), 회귀 없음
```

수동 E2E(실 프로세스 2개 + curl로 확인 완료):
1. `dotnet run --project GameServer` → `127.0.0.1:7777`(게임) + `127.0.0.1:7779`(텔레메트리) 리스닝 확인(`netstat`)
2. `dotnet run --project MonitorServer` → `127.0.0.1:7779`에 자동 접속(`netstat`으로 ESTABLISHED 확인)
3. `curl http://127.0.0.1:8080/` → HTTP 200, 대시보드 HTML
4. `curl http://127.0.0.1:8080/events` → 1초 간격으로 `data: {"connected":true,...}` camelCase JSON 스트림 확인

## 7. 향후 확장 포인트

- 플레이어별 행(레벨/골드/기여도) — §2.1의 스코프 제외 근거 해소를 위한 안전한 발행 경로(예: 세션
  제출 루프가 직접 자기 몫만 별도 채널에 쓰는 방식) 설계 후 추가
- 이벤트 로그 브라우저(`logs/game-events.ndjson` 검색) 탭, HP 추이 히스토리 차트
- 웹 인증/TLS, 제어판(레이드 리셋 등) — 양방향이 필요해지면 SSE 대신 WebSocket으로 승격
- `IdleRpg.GameServer` `System.Diagnostics.Metrics` 미터 → Prometheus 익스포터 통합(별도 트랙,
  현재는 `dotnet-counters`로만 소비 중)
