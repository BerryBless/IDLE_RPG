# 대규모 동시 연결 용량 하네스 설계 (2026-07-21)

## 1. 배경 및 목적

`tools/LoadTester`(단일 프로세스 부하 툴, `plan/loadtester_0721.md`)는 단일 소스 IP → 단일 서버
엔드포인트라 4-튜플(임시 포트) 제약상 동시 연결이 ~수만에서 상한된다. "유저 3백만 동시 접속"
요구를 받았으나, **단일 64GB 머신에서 동시 3M은 물리적으로 불가능**하다:

- 로컬 루프백은 연결당 소켓 2개(클라+서버, 둘 다 이 머신 커널) → 3M = 600만 소켓, 커널/유저 메모리 수백 GB
- Windows 단일 박스 실제 동시 소켓 상한은 튜닝해도 ~20~50만
- GameServer 공유 레이드 보스가 `SessionRegistry.BroadcastAsync`로 전 세션 O(N) 팬아웃(단일 드레인
  태스크) → 수십만만 돼도 붕괴

사용자 합의 하에 **순수 연결 용량 테스트(연결+HMAC 인증+유지) 하네스를 멀티프로세스·멀티포트로
구축하고, 현실 목표(~30만)를 안정 통과(PASS)로 검증**하는 것으로 확정했다. 3M은 다중 머신 분산이
필요하며 이 하네스가 그 워커 단위가 된다.

## 2. 설계 결정

| 항목 | 채택 | 사유 |
|---|---|---|
| 서버 린 용량 모드 | `IDLERPG_GAME_CAPACITY_MODE=1`(opt-in): 인증 후 레이드 미배선 | O(N) 브로드캐스트가 대규모 연결에서 붕괴 — 연결+인증+유지만 측정 |
| 멀티포트 | `IDLERPG_GAME_PORT_COUNT=P`: P개 리스너 인스턴스가 registry 공유 | 4-튜플 상한을 P배로 확장(연결 ≈ 임시포트수 × P) |
| 멀티프로세스 | 코디네이터가 워커 K개 스폰(`--workers K`), 클라이언트 샤딩 | 여러 프로세스로 분할(사용자 요구) + 코어 활용 + 크래시 격리 |
| IPC | 워커 stdout `@interval`/`@final` 라인 프로토콜 | 자식 프로세스라 `Process.OutputDataReceived`로 무폴링 수신 |
| 포트 기본값 | 게임 포트 20000(임시 포트 범위 위) | 리스너가 임시 포트 할당과 충돌(WSAEACCES 10013)하지 않게 — 소규모는 admin 불필요 |
| 판정 | `CapacityVerdictEvaluator` 5규칙(피크 도달·유지 안정·오류율·서버 건강·워커 무결성) | 용량 관점 전용 — 기존 5규칙(RTT 등)과 분리 |

## 3. 컴포넌트 구조

```
GameServer/Main.cs                     capacityMode 분기·P리스너 루프·telemetry 직접 기동
GameServer/Systems/TelemetryPublisher  IReadOnlyList<IServerListener> 합산(멀티포트 접속 수)
tools/LoadTester/
├─ Coordination/
│  ├─ WorkerShard.cs          ForWorker(분할)·SelectPort(라운드로빈) 순수 함수
│  ├─ WorkerStatus.cs         @interval/@final 라인 record + WorkerLineProtocol(직렬화/파싱)
│  ├─ StatusLineEmitter.cs    IIntervalReporter 구현 — 워커가 @interval stdout 출력
│  ├─ WorkerProcessLauncher   Environment.ProcessPath 재기동(dotnet/apphost 양쪽), DOTNET_GCHeapCount=4
│  ├─ CombinedAggregator.cs   워커 라인 흡수·합산(@final 시 라이브 상태 제거 → 종료 오탐 방지)
│  ├─ CombinedInterval.cs     콘솔·NDJSON·판정 공용 집계 계약
│  ├─ CombinedNdjsonWriter    통합 combined.ndjson(runStart/interval/runEnd)
│  └─ CoordinatorRunner.cs    K워커 스폰·집계 루프·최종 판정·종료코드
├─ Verdict/CapacityVerdictEvaluator.cs  용량 5규칙 판정
├─ Options/LoadTestOptions.cs  --workers/--role/--worker-index/--port-count/--client-index-offset/--capacity/--target-concurrent
├─ Output/IIntervalReporter.cs 리포터 추상화(ConsoleReporter/StatusLineEmitter)
├─ Client/VirtualClient.cs     _targetPort = SelectPort(GamePort, PortCount, globalIndex)
└─ Client/LoadController.cs    globalIndex = ClientIndexOffset + i
capacity-tune.bat              (admin) 임시 포트 범위 확장 netsh
capacity-test.bat              원클릭: Release 빌드 → 린 서버 기동 → 코디네이터 → 정리
```

**스레딩**: 락 0 유지. 워커 stdout 콜백(멀티 스레드) → `CombinedAggregator`(ConcurrentDictionary) →
코디네이터 집계 루프(단일). 서버 텔레메트리·리소스 샘플링은 코디네이터만(K프로세스 중복 방지).

## 4. 핵심 사용 패턴

```
# 0회차: 관리자 권한으로 임시 포트 범위 확장(대규모 실행 시 필수)
capacity-tune.bat            # netsh int ipv4 set dynamicport tcp start=10000 num=55000

# 원클릭 용량 테스트 [clients] [workers] [ports] [duration]
capacity-test.bat 300000 8 8 10m
capacity-test.bat 400 2 2 60s        # 스모크

# 수동(서버 별도 기동 후)
set IDLERPG_GAME_CAPACITY_MODE=1 & set IDLERPG_GAME_PORT=20000 & set IDLERPG_GAME_PORT_COUNT=8
dotnet run -c Release --project GameServer
dotnet run -c Release --project tools/LoadTester -- --mode game --capacity ^
  --clients 300000 --workers 8 --port-count 8 --game-port 20000 --telemetry-port 19999 ^
  --duration 10m --ramp-up 4000 --server-process GameServer
```

- 연결 상한 공식: **동시 연결 ≈ 소스 포트 수(S) × 서버 포트 수(P)**. 단, 이는 클라이언트가 소스
  포트를 **명시 바인드(SO_REUSEADDR)**해 P개 목적지에 재사용할 때만 성립한다(아래 §7 실측 참고).
  기본 `connect()`는 소스 포트를 재사용하지 않아 임시 풀(~14k)이 전역 상한이 된다.
- 종료 코드: 0 PASS · 1 FAIL · 2 사용법 · 3 중단
- 통합 시계열: `logs/capacity/combined.ndjson`(전 워커 합산+서버 관측), 워커별 `worker-{i}/`.

## 5. 판정 규칙 (CapacityVerdictEvaluator)

① **피크 도달**: 최대 Σauth ≥ target(미달 시 달성 최대치 = 실측 상한, 사유에 명시).
   교차: 서버 텔레메트리가 클라 인증보다 1% 초과 적게 본 구간 존재 시 FAIL(세션 유실 의심).
② **유지 안정**: 램프 완료(피크 ≥ 0.99×target) 이후 평균 유지율 ≥ 0.99 && 순간 최저 ≥ 0.97.
③ **오류율**: Σfail / Σattempt ≤ 0.5%.
④ **서버 건강**: 프로세스 미소실 · WS 상한 · 텔레메트리 장기 침묵 없음.
⑤ **워커 무결성**: 전 워커 interval≥1 보고 · @final 전달 · 종료코드 0/1/3.

종료 오탐 방지: @final 수신 시 워커 라이브 상태 제거 + 워커 종료 틱은 관측 제외(정상 종료가 대량
끊김으로 오판되지 않게).

## 6. 변경/신규 파일

- 서버: `GameServer/Main.cs`(용량 분기·P리스너·포트 충돌 가드), `Systems/TelemetryPublisher.cs`(리스트 합산)
- LoadTester: `Coordination/` 7파일, `Verdict/CapacityVerdictEvaluator.cs`, `Output/IIntervalReporter.cs`,
  기존 수정(Options·Program·VirtualClient·LoadController·ConsoleReporter·MetricsSampler)
- 배치: `capacity-tune.bat`, `capacity-test.bat`
- 테스트: `WorkerShardTests`·`WorkerStatusTests`·`CombinedAggregatorTests`·`CapacityVerdictEvaluatorTests` +
  `LoadTestOptionsTests` 확장 (LoadTester.Tests 84→123)

## 7. 실측으로 정정된 연결 상한 (중요)

**초기 가정 오류 정정**: "동시 연결 ≈ 임시 포트 × P"라는 가정은 틀렸다. Windows는 기본
`connect()`에서 **아웃바운드 소켓마다 임시 소스 포트를 전역적으로 하나씩 소비**하므로, 서버
포트를 P개 열어도 소스 포트를 재사용하지 않으면 단일 소스 IP의 임시 풀(~14k)이 전역 상한이다.

**실측 1 (소스 포트 미바인딩, 50k/8포트)**: 정확히 **~13,668에서 정체**, 에러 폭증 → FAIL.
하네스가 실측 상한을 정확히 감지·보고함(달성 최대 = 실측 상한).

**검증된 해법 (관리자 권한 불필요)**: 클라이언트가 소스 포트를 **명시 바인드 + SO_REUSEADDR**하면
같은 소스 포트를 서로 다른 목적지 포트에 재사용할 수 있다(단일 프로세스·**프로세스 간 모두 실측 성공**).
따라서 `IClientConnection.LocalEndPoint`(신규)로 `sourcePort = base + globalIndex/P`,
`dstPort = gameBase + globalIndex%P`를 바인드하면 (srcPort, dstPort) 4-튜플이 전역 유일하면서
**동시 연결 ≈ S × P**로 확장된다. S = 사용한 소스 포트 수(클린 밴드 25000~49999 ≈ 25000개),
P = 서버 포트 수. 예: P=12 → 25000 × 12 = **300k**를 관리자 권한·임시 범위 확장 없이 달성 가능.

**소스 포트 밴드 주의**: Windows 예약 범위(`netsh int ipv4 show excludedportrange tcp`, 이 머신은
8522~8721·50000~50059)와 서버 리스너 포트·OS 임시 풀(~1024~15000)을 피해야 한다. 기본
`--source-port-base 25000` + 300k/P=12면 소스 포트가 25000~49999에 들어가 전부 회피된다.

연결당 메모리: 서버 ~25~40KB + 클라 ~20~35KB + 커널 ~10~20KB → 300k에서 총 ~20~30GB(64GB 여유).
예상 병목: ① 소스 포트 밴드 사이징(예약 범위 회피) → ② PING 스톰(`--ping-interval 30s`) →
③ 램프 인증/GC(`DOTNET_GCHeapCount=4`) → ④ 메모리.

검증(실측):
- `dotnet build IDLE_RPG.sln` 0오류, `dotnet test` LoadTester.Tests 123/123 + 기존 스위트 회귀 없음
- 2워커×2포트×400 스모크: 400/400 인증·텔레메트리 교차 일치·PASS·정상 정리
- 소스 포트 재사용: 단일·프로세스 간 모두 실측 성공
- 실측 상세는 §8

## 8. 실측 결과 (2026-07-21, 단일 64GB/32코어 Windows)

| 실행 | 결과 |
|---|---|
| 2워커×2포트×400, 40s | **PASS** — 400/400 인증·텔레메트리 정확 일치·정상 종료 |
| 8워커×8포트×50k(소스포트 미바인딩) | **FAIL/정체 ~13,668** — 임시 포트 풀 고갈(설계 가정 반증). 하네스가 실측 상한을 정확히 감지 |
| 8워커×8포트×50k(소스포트 재사용) | **50,000/50,000 100% 유지, 에러 0** — 14k 벽 돌파(3.6배). 텔레메트리 정확 일치 |
| 8워커×8포트×40k, 50s | **PASS** — 40,000/40,000·유지율 100%·에러 0·전 워커 @final·코디네이터 정상 완료(EXIT 0) |
| 8워커×12포트×300k 목표 | **피크 ~99-101k에서 포화**(auth 99,273 ≈ 텔레메트리 99,652, 0.4% 일치). 서버 CPU 1%·메모리 여유 → **Windows 루프백 네트워크 스택 한계**(논페이지드 풀/AFD 추정). 96k 부근부터 에러 급증 |

**결론**: 이 단일 박스의 실측 동시 연결 상한은 **~95-100k**(소스 포트 재사용 없이는 ~14k, **약 7배**
개선). 안정적으로 100% 유지되는 구간은 ~40-50k. 병목은 서버 컴퓨트가 아니라 클라이언트+루프백
네트워크 스택. **동시 3M은 물리적 불가**가 실측으로 재확인됨 — 진짜 대규모는 다중 머신(이 하네스를
머신당 ~50-90k 워커 단위로) 또는 다중 소스 IP(루프백 별칭, 관리자 설정 필요)가 필요.

버그 수정(실측 중 발견): ① 종료 시 워커 stale 상태로 인한 텔레메트리/유지율 오탐(→ @final 시 라이브
상태 제거 + 종료 틱 관측 제외 + 텔레메트리 부족분 2구간 연속 확정) ② 워커 1개 셧다운 hang 시
코디네이터 무한 대기(→ duration+30s 데드라인 후 잔여 워커 강제 종료).

## 9. 향후 확장 포인트

1. 다중 머신 분산: 이 코디네이터/워커를 N머신에 배포해 진짜 동시 3M(머신당 ~30만 × 10머신)
2. 린 클라이언트 트랜스포트: 순수 용량에서 Pipe 없는 초경량 소켓으로 클라측 메모리 추가 절감
3. 용량 모드 스톨 지표 개선: 앱 트래픽이 없어 스톨이 100%로 표시됨(판정엔 무영향) — PONG/RTT
   신선도 기반 liveness로 대체 검토
4. `capacity-tune`의 TCP TIME_WAIT 단축(churn 심한 재실행 시)
