# LoadTester — 성능 측정·부하 테스트 콘솔 툴 설계 (2026-07-21)

## 1. 배경 및 목적

GameServer/AuthServer에는 성능을 측정할 도구가 없었다(BenchmarkDotNet·부하 생성기 부재).
요구는 두 가지 — **핑(RTT) 측정**과 **최대 트래픽에서의 안정성 검증(72시간 이상 기능 유지)**.
프로토콜이 커스텀 바이너리 프레이밍(4바이트 헤더 `[ushort packetId LE][ushort bodyLen LE]`)이라
k6 등 범용 부하 도구는 사용할 수 없고, ServerLib 클라이언트(`ServerNet.CreateClient()`)에
PING/PONG 기반 `Rtt` 측정이 이미 내장돼 있어 이를 재활용하는 전용 콘솔 툴을 신규로 만들었다.

## 2. 설계 결정

| 항목 | 채택 | 기각 대안 | 사유 |
|---|---|---|---|
| 구현 형태 | 전용 콘솔 툴(`tools/LoadTester`, ServerLib만 참조) | NBomber 연동 / xUnit 장시간 테스트 | 외부 의존성 0 유지·72h 운영 제어(콘솔 리포트·Ctrl+C 드레인)는 테스트 러너로 불가 |
| 부하 경로 | `--mode game`(HMAC 직접 발급) / `--mode full`(AuthServer 로그인) 선택식 | 단일 경로 고정 | Mongo 없이도 GameServer만 조준 가능 + 실제 전체 파이프라인 측정 둘 다 필요 |
| RTT 수집 | **샘플링**: 샘플러가 리포트 주기마다 전 클라이언트 `client.Rtt`(volatile) 읽기 | PONG 이벤트마다 기록 | 구간별 클라이언트 분포가 백분위 임계치 판정에 정확하고 I/O 스레드 부담 0 |
| 백분위 자료구조 | 고정 793버킷 하이브리드 해상도 히스토그램(≈6.3KB, 단일 라이터라 무동기화) | 전체 샘플 보관 / HdrHistogram 패키지 | 72h 메모리 상수 상한 + 의존성 0 |
| 핫 카운터 | `StripedLongCounter`(코어별 캐시라인 패딩 슬롯) | 단일 필드 Interlocked | 초당 수만 회 증가 시 캐시라인 핑퐁 제거. 저빈도(연결/인증)는 단일 Interlocked 유지 |
| 토큰 재사용(full) | 계정별 토큰 캐시(만료 5분 전까지) + `SemaphoreSlim` 동시 로그인 상한 | 재접속마다 로그인 | PBKDF2 CPU·임시 포트 보호. 토큰 검증은 접속 시 1회라 유지 세션은 만료 무관 |
| 스톨 판정 | "인증된 전원 스톨 && 서버 ConnectedCount>0"이 **2구간 연속**일 때만 인시던트 | 개별 클라이언트 스톨 즉시 FAIL | 보스 리스폰 공백·개별 지연의 오탐 방지 |
| 재접속 | 지수 백오프(3s×2, cap 60s, ±20% 지터) + 공유 `ConnectPacer`(초당 N + 동시 핸드셰이크 64) | 즉시 재접속 | 서버 재시작 시 10k 동시 재접속 thundering herd 방지 |

## 3. 컴포넌트 구조

```
tools/LoadTester/
├─ Program.cs                  조립·수명(duration ∪ Ctrl+C)·15s 드레인·최종 리포트·종료코드
├─ Options/LoadTestOptions.cs  CLI 파싱(불변 record) + 기간 파서(72h/30m/60s/500ms)
├─ Auth/
│  ├─ CredentialProvider.cs    clientIndex % accounts → user{i:D4}/Pass!{i:D4} (시딩 규칙 로컬 미러)
│  ├─ ITokenSource.cs          토큰 획득 전략 인터페이스(TokenResult)
│  ├─ LocalHmacTokenSource.cs  game 모드: HmacAuthTokenCodec 직접 발급
│  └─ AuthServerTokenSource.cs full 모드: 로그인 왕복 + 계정별 캐시 + 동시성 게이트
├─ Client/
│  ├─ VirtualClient.cs         접속당 상태머신(토큰→접속→인증→수신 유지→끊김 분류→백오프)
│  ├─ ConnectPacer.cs          CAS 슬롯 예약 토큰버킷 + SemaphoreSlim 동시 핸드셰이크 상한
│  ├─ ReconnectPolicy.cs       지수 백오프+지터(Random 주입, 결정적 테스트)
│  └─ LoadController.cs        N개 생성·기동·종료 대기
├─ Metrics/
│  ├─ StripedLongCounter.cs    코어별 스트라이프 핫 카운터
│  ├─ RttHistogram.cs          고정 793버킷 백분위 히스토그램(단일 라이터)
│  ├─ MetricsAggregator.cs     공유 카운터 집합(핫/저빈도 2계층)
│  ├─ MetricsSampler.cs        유일한 주기 관측 루프 → IntervalReport 생산
│  ├─ IntervalReport.cs        콘솔·NDJSON·판정 공용 데이터 계약
│  └─ ResourceMonitor.cs       서버(PID/이름) WS·CPU% + 자기 자신(WS/스레드/Gen2) 샘플
├─ Telemetry/TelemetrySubscriber.cs  7779 구독(재접속 루프, volatile 최신값) — 서버측 교차 검증
├─ Output/
│  ├─ NdjsonMetricsWriter.cs   runStart/interval/runEnd 3종 레코드, 크기·정시 로테이션, STJ 소스생성
│  └─ ConsoleReporter.cs       구간당 1줄 요약
└─ Verdict/VerdictEvaluator.cs 구간 관찰 누적 + 5규칙 판정(PASS/FAIL + 사유)
```

**스레딩 모델**: 락 0. I/O 콜백 N개(무블로킹: 패킷 ID 스위치+카운터+Volatile.Write만) +
샘플러 태스크 1개(히스토그램·NDJSON·판정기의 유일한 접근자 — 동기화 생략의 근거) +
텔레메트리 루프 1개. 통신은 Interlocked/Volatile/TCS(RunContinuationsAsynchronously)만 사용.

## 4. 핵심 API / 사용 패턴

```
# game 모드(Mongo 불필요, GameServer만): DEBUG 빌드끼리는 시크릿 자동 합의
dotnet run --project tools/LoadTester -- --mode game --clients 100 --duration 60s --server-process GameServer

# full 모드(AuthServer+Mongo 필요, 시딩 계정 3000 사용)
dotnet run --project tools/LoadTester -- --mode full --clients 3000 --ramp-up 300 --duration 30m

# 72시간 소크(권장: Release 빌드 + 시크릿 명시)
set IDLERPG_AUTH_HMAC_SECRET=<32바이트 이상>
dotnet run -c Release --project tools/LoadTester -- --mode game --clients 10000 --ramp-up 500 --duration 72h --server-process GameServer --server-max-ws-mb 4096
```

- 종료 코드: `0` PASS · `1` FAIL · `2` 사용법/구성 오류 · `3` 지속시간 전 중단(Ctrl+C)
- FAIL 규칙(전부 CLI 재정의): ① 누적 RTT p50>100ms/p95>250ms/p99>500ms ② 오류율>0.1% 또는
  미인증 클라이언트 존재 ③ 평균 유지율<99% ④ 전면 스톨 인시던트 ≥1 ⑤ (모니터링 시) 서버
  워킹셋 상한 초과·프로세스 소실
- NDJSON: `logs/loadtest-{stamp}.ndjson` — `runStart`(옵션 에코) / `interval`(10s) / `runEnd`(판정).
  256MB 또는 정시 경계에서 로테이션.
- Windows 10k+ 주의: 동적 포트 기본 16,384개 — `--clients>12000` 경고 출력,
  `netsh int ipv4 set dynamicport tcp start=10000 num=55535`로 확장 권장.

## 5. 변경 파일 목록

- 신규 `tools/LoadTester/` 19파일(§3 트리) — ServerLib만 참조, ServerGC 활성
- 신규 `tests/LoadTester.Tests/` 10파일: 옵션·히스토그램(버킷 경계/백분위/리셋)·스트라이프
  카운터(병렬 정확성)·백오프(시드 결정성)·계정 매핑·판정 5규칙·NDJSON(스키마/로테이션)·
  로컬 토큰(코덱 왕복)·AuthServer 토큰(가짜 리스너: 성공/거부/미기동/캐시/동시성)·
  실소켓 E2E(인프로세스 인증 게이트+레이드 픽스처: 전원 인증·브로드캐스트·RTT·서버 중단 분류)
- 수정: `IDLE_RPG.sln`만. **서버 코드 무변경.**

## 6. 빌드 검증

```
dotnet build IDLE_RPG.sln          # 0 오류 0 경고
dotnet test IDLE_RPG.sln           # LoadTester.Tests 84/84, 기존 스위트 회귀 없음
                                   # (IdleRpg.HarnessTests 3건 실패는 기존 worklog-writer 고아 드리프트 — 본 작업 무관)
```

실측 스모크(로컬 GameServer DEBUG):
- 50 클라이언트 20s: PASS, RTT p50 0.2ms/p99 2.1ms, tele=conn 교차 일치, 브로드캐스트 5,911건
- 500 클라이언트 30s: PASS, 브로드캐스트 85,809건(3.0k/s), 서버 WS 64MB
- 네거티브(실행 중 GameServer 강제 종료): 끊김 50 전부 분류·재접속 100회(백오프)·
  `srv LOST` 표시·FAIL(사유 3건: 오류율/유지율/프로세스 소실)·exit 1

## 7. 향후 확장 포인트

1. ~~**10k Release 램프 실측**~~ → **완료(2026-07-21 실측)**: Release 빌드
   `--clients 10000 --ramp-up 500 --duration 10m` — **PASS**(exit 0). 램프 20초 만에
   10,000/10,000 전원 인증 수렴, 누적 RTT p50 0.4ms/p95 0.8ms/p99 1.1ms, 오류·끊김·전면 스톨
   0건, 브로드캐스트 1,939만 건(≈33k/s)·495MB 수신, RejectedConnections 0, 텔레메트리
   교차값(tele=conn 10000) 전 구간 일치. 서버 워킹셋 168→252MB(CPU 2~3%), 툴 자체
   128→166MB(Gen2 1회) — 10분 내 완만 상승으로 누수 징후는 아니나 **8h 리허설에서 추세 재확인
   필요**. 포트는 루프백 2만 소켓으로 동적 범위(13,977개) 내 소화(양끝단이 한 머신이라 실측
   가능했음). 다음: 72h 전 8h 야간 리허설
2. **full 모드 대규모 실측**: Docker 스택(up.bat) + 시딩 3000계정으로 전체 파이프라인 부하
3. 텔레메트리 스냅샷 신선도 표시(현재 서버 사망 후에도 마지막 값 유지 — 수신 시각 기반 stale 마킹)
4. 다중 머신 분산 부하(단일 머신 포트/CPU 한계 초과 시) — NDJSON 병합 리포트
5. MonitorServer 대시보드에 부하 테스트 지표 연동
