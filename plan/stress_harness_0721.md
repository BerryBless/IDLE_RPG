# 스트레스 테스트 하네스 설계 (2026-07-21)

## 1. 배경 및 목적

용량 하네스(`plan/capacity_harness_0721.md`, 안정 최대치 측정) 완료 후 스트레스 테스트를 추가했다.
스트레스는 "숫자 도달"이 아니라 **정상 범위를 넘겨 서버를 깨뜨리고, 어떻게 무너지고 회복하는가**를
본다. 4 시나리오(스파이크/버스트, 연결 churn, 비정상/악성 패킷, slowloris/정체 피어)를 **측정 전용**
(서버 무변경)으로 구현하고, 각 실행에 **정상 대조군 프로브**(connect+auth+hold 소수 클라이언트)를
두어 스트레스 전/중/후 서비스 건강도와 회복을 증명한다.

## 2. 검증된 핵심 사실

- 세션은 **accept 시점 등록**(SocketPipelineListener) → 텔레메트리 ConnectedCount가 미인증·정체·악성
  세션까지 카운트 → 누적·회복 신호로 유효.
- `IdleTimeout`/`MaxConnections`/진행기반 idle sweep는 **ServerLib에 이미 구현**돼 있으나 Main.cs
  미배선 → 정체/악성 세션이 서버측에서 능동 축출되지 않음(방어는 Main.cs 1줄, 측정 전용이라 안 함).
- 악성 패킷 "거부"는 연결 종료가 아니라 **앱계층 ack-false/무시**(SessionAuthGate가 디코드 예외 자체
  catch) → 세션이 열린 채 유지. 원시 송신 `SendAsync(ReadOnlyMemory<byte>)`로 잘못된 프레임 직접 조립.

## 3. 아키텍처

**단일 제어 프로세스 `StressRunner`** — 페이즈 상태머신(Baseline→During→Release→Recovery) + 대조군
프로브 + 활성 시나리오 + 2초 샘플러 + 판정기. `CoordinatorRunner`는 페이즈 미인지라 그 빌딩블록
(WorkerProcessLauncher/WorkerShard/CombinedAggregator)만 버스트/churn에서 재사용.

```
tools/LoadTester/Stress/
├─ IStressScenario.cs          Kind/Model/Expectations/DriveAsync/ReleaseAsync/Snapshot
├─ StressRunContext.cs         옵션·토큰·텔레메트리·리소스·출력
├─ StressRunner.cs             페이즈 머신·샘플러·판정·리포트·종료코드
├─ StressPhase(Clock).cs       순수 페이즈 매핑·회복 판정(IsRecovered)
├─ ControlProbe.cs             VirtualClient N개 재사용·격리 메트릭·PortCount=1·PING 1s
├─ StressIntervalReport.cs / StressNdjsonWriter.cs   구간 계약·NDJSON
├─ StressVerdictEvaluator.cs   생존·프로브·회복 게이트(기대 플래그로 회복 게이트 on/off)
├─ MalformedFrames.cs          7종 악성 프레임 순수 빌더
├─ Clients/{AdversarialClient,StalledPeer}.cs
└─ Scenarios/{Malformed,Slowloris,Burst,Churn}Scenario.cs + WorkerFleet.cs
```

| 시나리오 | 모델 | 규모 | 클라이언트 |
|---|---|---|---|
| burst | 멀티프로세스 | 과부하 | VirtualClient(초고 ramp, 소스포트 재사용) |
| churn | 멀티프로세스 | 고속 | VirtualClient `--churn`(인증 후 즉시 종료·재접속) |
| malformed | 인프로세스 | 수천 | AdversarialClient(원시 악성 프레임 플러드) |
| slowloris | 인프로세스 | 수천 | StalledPeer(무송신 또는 1B/s 드립) |

## 4. 판정 (StressVerdictEvaluator)

- **① 생존(전 시나리오 하드 게이트)**: 프로세스 생존·텔레메트리 응답·크래시 없음
- **② 프로브 건강 During(하드 게이트)**: 최저 연결≥95%·인증≥90%·RTT p95 ≤ max(3×기준, 기준+50ms)
- **③ 회복(burst/churn만 게이트)**: RTT p95 ≤ 1.5×기준 & 서버 세션 수 기준±5% 복귀 & WS ≤ 기준×1.2.
  **malformed/slowloris는 리포트만**(누적이 예상 현상 — 게이트하면 정상 생존 서버가 FAIL).

## 5. 실측 결과 (2026-07-21, 단일 64GB/32코어 Windows)

| 시나리오 | 판정 | 핵심 발견(실측) |
|---|---|---|
| **malformed** | **FAIL** | 서버 **생존**(260만 악성 프레임 처리, 크래시 없음). 그러나 ① 정상 클라 **RTT p95 0.4ms→1.5초 폭증**(악성 플러드가 legit 서비스 저하 — FAIL 사유) ② 서버 WS **39MB→3.2GB**(오버사이즈 length 헤더가 파이프 버퍼를 무한 축적, **피어당 ~3.2MB** 메모리 증폭). 해제 후 세션은 정리(tele 1050→50)되나 WS는 즉시 미회수 |
| **slowloris** | **PASS** | 서버 생존·프로브 100% 건강(RTT 0.7ms). 조용한 정체 피어 2000개는 **피어당 6.7KB**로 값싸게 누적(CPU 무소비 → legit 무영향). idle sweep 미배선이라 서버가 능동 축출 안 함(클라 종료 시엔 정리) |
| **burst** | **PASS** | 4만 급습 스파이크 → 프로브 100% 유지(RTT 1.6ms), 서버 생존, 해제 후 **+4초 회복**(세션·WS 기준선 복귀, 누수 없음). 피크 서버접속 40,100 |
| **churn** | **PASS** | 고속 재접속 → **서버 세션 수 낮게 안정**(피크≈프로브 100 — 접속 즉시 종료라 누적 안 됨), 생존·프로브 건강. 병목은 클라측 TIME_WAIT/임시 포트 |

**대표 발견 2가지(실무 가치)**:
1. **악성 패킷 메모리 증폭**: 오버사이즈 length 헤더 + 연속 플러드가 파이프 버퍼를 피어당 수 MB까지
   키워 소수 악성 연결로 서버 WS를 GB급으로 밀어올린다. 또한 legit 클라 RTT를 초 단위로 저하.
2. **정체 세션 무축출**: idle sweep 미배선이라 서버가 진행 없는 세션을 스스로 끊지 않는다(값은 싸지만
   축적 가능).

## 6. 하드닝 적용 (opt-in, Main.cs만 — ServerLib 무변경)

스트레스가 드러낸 약점에 대한 방어를 **env 토글 opt-in**으로 배선(기본 미설정=기존 동작, before/after
비교 가능). ServerLib에 이미 구현돼 있던 기능을 Main.cs에서 켜기만 함:

- `IDLERPG_GAME_IDLE_TIMEOUT_SECONDS=N`: `listener.IdleTimeout` 배선. 진행 기준이 **`LastProgressAt`**
  (완전 패킷 파싱 시에만 갱신, 라인 189가 dispatch 전)이라 **PING하는 정상 클라는 안전**하고
  slowloris(무송신)·악성 미완성 플러드(완전 프레임 미완성)는 진행 미갱신이라 스윕된다. 반드시 정상
  클라의 PING 주기보다 커야 함. `OnIdleTimeout`으로 축출 수 집계.
- `IDLERPG_GAME_MAX_CONNECTIONS=N`: `listener.MaxConnections` 배선(초과 연결은 세션 할당 전 거부 — 저비용).
- `IDLERPG_GAME_MAX_FRAMES_PER_SECOND=N`: **세션당 프레임 레이트 리밋(신규, ServerLib 변경)**. 수신
  루프가 고정 1초 윈도우로 완전 패킷 수를 세어 상한 초과 시 해당 세션만 종료. 위 진행 타임스탬프를
  재사용해 정상 트래픽엔 필드 비교 2개뿐(무할당·무동기, 단일 수신 스레드). `IServerListener.
  SessionMaxFramesPerSecond`·`SocketPipelineSession.MaxFramesPerSecond` 추가. **단일 연결 프레임
  플러드는 완전 차단**(E2E 테스트로 검증: 과다 전송 세션 축출·저빈도 정상 세션 유지).

**검증된 사실(설계 근거)**: 세션 accept 시점 등록 → 텔레메트리가 정체 세션 카운트. 유휴 스윕은 세션을
`DisposeAsync`로 하드 종료. 완전 malformed 패킷은 디코드 예외로 이미 자동 축출(SocketPipelineSession
라인 190-209). 단일 프레임 본문은 ushort(≤64KB)+Pipe 백프레셔로 이미 상한 — 증폭 벡터는 **연결 수**.

### Before/After 실측 (2026-07-21)

**Slowloris(무송신 정체 피어 3000, IdleTimeout=8s)** — 완전한 방어:

| | BEFORE(하드닝 없음) | AFTER(IdleTimeout=8s) |
|---|---|---|
| During 지속 서버 접속 | **3,050 무한 유지** | 순간 3,050 → **8초 내 전량 축출 → 50(프로브만)** |
| 서버 방어 | 없음(수동적 유지) | 능동 유휴 스윕 |

**Malformed(적대적 800)** — 3종 하드닝 계층 적용:

| | BEFORE(방어 없음) | AFTER(IdleTimeout=8s + MaxConn=150 + MaxFramesPerSecond=30) |
|---|---|---|
| 서버 WS | **3,047MB** | **731MB (−76%)** |
| 서버 접속 | 850 | **150(상한 적용)** |
| 정상 클라 RTT p95 | 3,125ms | **1,415ms (−55%)** |
| 정상 클라 연결·인증 | 100%·100% | 100%·100% (서비스 유지) |

**정직한 결론**:
- **slowloris(연결 유지형)** — IdleTimeout으로 **완전 방어**(진행 없는 세션을 8초 내 능동 축출).
- **단일 연결 프레임 플러드** — MaxFramesPerSecond로 **완전 차단**(E2E 검증).
- **malformed 분산 재접속 플러드** — 3종 계층으로 **메모리 −76%·연결 상한·RTT −55%**, 정상 클라는
  전 구간 100% 연결·인증(서비스 유지). 다만 RTT는 완전히 기준선으로 돌아오지 않는다. **잔여 부하는
  개별 프레임이 아니라 800 클라이언트의 재접속 폭풍**(700개가 거부되지만 TCP 핸드셰이크·accept 비용은
  발생)이다. 이를 없애려면 **소스 IP별 연결 레이트 리밋**(WAF/방화벽급)이 필요한데, 단일 루프백 IP
  테스트로는 실증 불가(MaxConnectionsPerIp는 정상 클라까지 함께 상한). 실운영에선 IP별 연결 레이트
  리밋·SYN 쿠키·L4 방화벽이 이 계층을 담당한다.

**요약**: 하드닝은 연결 유지형·단일 연결 플러드를 완전 차단하고, 분산 플러드도 메모리/연결/지연을 크게
낮춰 **서버 생존 + 정상 클라 서비스 유지**를 달성한다. 완전한 무영향은 애플리케이션 계층 밖(네트워크
경계)의 방어가 함께 필요하다.

**향후 과제**: 소스 IP별 연결 레이트 리밋, 무효 프레임 N회 후 `session.DisposeAsync()` 즉시 축출
(server ISession엔 Disconnect 없음, DisposeAsync가 축출 API), 본문 길이 프로토콜 상한(현 65535).

## 7. 사용법 / 검증

```
stress-test.bat [scenario] [probe] [stress] [duration]   # 원클릭
dotnet run --project tools/LoadTester -- --stress malformed --probe-clients 100 \
  --stress-clients 4000 --baseline-duration 20s --stress-duration 30s --recovery-duration 40s \
  --game-port 20000 --server-process GameServer
```
종료 코드 0 PASS / 1 FAIL / 3 중단. NDJSON: `logs/stress/stress-<scenario>.ndjson`(4페이즈 태그).

- `dotnet test` LoadTester.Tests 152/152(스트레스 신규: MalformedFrames·StressVerdict·StressPhaseClock·
  malformed E2E), 기존 스위트(GameServer 191·AuthServer 36·Echo 13) 회귀 없음(서버 무변경)
- 4 시나리오 실서버 스모크 전부 실행(위 §5)

## 8. 향후 확장

1. 하드닝 적용 후 동일 스트레스로 before/after 비교(방어 효과 정량화)
2. 혼합 카오스 시나리오(여러 스트레스 동시)
3. 악성 프레임 변종 추가(압축 폭탄류 등, 프로토콜 확장 시)
