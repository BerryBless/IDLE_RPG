# 하네스 라이브 스모크 통합 스코어카드

집계 시각: 2026-07-04 | 소스: `tests/results/*/scorecard.json` (전부 `IdleRpg.Grader` 실제 산출)
6개 하네스, 7회 라이브 실행(commitandpush는 음성/양성 2회) 전부 라이브 LLM 실행 결과 — 합성 데이터 없음.

## 종합

| 하네스 | 성격 | 판정 | 상세 |
|---|---|---|---|
| gc-guard | 분석형 | ✅ PASS | recall 8/8 (100%) · 교차검증 확정점수 0/100 |
| code-review | 분석형 | ✅ PASS | recall 9/9 (100%) |
| concurrency-guard | 분석형 | ✅ PASS | recall 8/8 (100%) · 교차검증 확정점수 3/100 |
| tdd | 생성형 | ✅ PASS | 신호 2/2 (dotnet test green · 진화 리포트 생성) |
| pipeline | 생성형 | ✅ PASS | 신호 2/2 (APPROVE · 아키텍처 문서 생성) |
| commitandpush (음성) | 생성형 | ✅ PASS | 신호 1/1 (보안 FAIL로 정상 차단, 커밋 0건) |
| commitandpush (양성) | 생성형 | ✅ PASS | 신호 3/3 (PASS→커밋메시지→커밋 `0b06ded` 생성) |

**7/7 전부 PASS.**

## 분석형 3개 — 결함 항목별 recall

전부 100% — 심어놓은 결함 25개(gc-guard 8 + code-review 9 + concurrency-guard 8) 중 25개 전부 산출 findings JSON에서 확인됨.

| 하네스 | 결함 ID (전부 FOUND) |
|---|---|
| gc-guard | GC1 GC2 GC3 GC4 GC5 GC6 GC7 GC8 |
| code-review | CR1 CR2 CR3 CR4 CR5 CR6 CR7 CR8 CR9 |
| concurrency-guard | CC1 CC2 CC3 CC4 CC5 CC6 CC7 CC8 |

## 생성형 4개 — 신호별 판정

| 하네스 | 신호 | 판정 | 세부 |
|---|---|---|---|
| tdd | `03_qa/test_results.txt` | PASS | 모든 필수 문자열 발견, 금지 문자열 없음 (통과 존재·실패 부재) |
| tdd | `04_evolution/evolution_report.md` | PASS | 패턴 매칭 성공 |
| pipeline | `03_load_test_audit.md` | PASS | 모든 필수 문자열 발견 (APPROVE) |
| pipeline | `04_pipeline_architecture.md` | PASS | 패턴 매칭 성공 |
| commitandpush(음성) | `01_security_result.md` | PASS | 모든 필수 문자열 발견 (FAIL 확인 — 차단 정상 작동) |
| commitandpush(양성) | `01_security_result.md` | PASS | 모든 필수 문자열 발견 (PASS 확인) |
| commitandpush(양성) | `02_commit_message.txt` | PASS | 패턴 매칭 성공 |
| commitandpush(양성) | `03_push_result.md` | PASS | 패턴 매칭 성공 |

## 라이브 실행에서만 드러난 발견 (합성 자기증명으로는 못 잡았을 것들)

1. **tdd 픽스처의 로케일 가정 오류** — `expected.json`이 영어 `dotnet test` 출력(`"Failed: 0"`)을 가정했으나 실제 환경은 한국어 로케일로 출력하며 실패 0건이면 `실패:` 줄 자체가 없음. 채점기 쪽 수정 완료.
2. **tdd-orchestrator 하네스 자체의 실제 버그** — `TddSession.csproj` 템플릿에 `EnableDefaultCompileItems=false` 누락으로 `NETSDK1022`(Compile 중복 항목) 빌드 실패가 매 TDD 사이클마다 재발할 수 있었음. `.claude/skills/tdd-orchestrator/SKILL.md` 템플릿에 영구 반영 완료.
3. **pipeline-architect의 감독자 개입이 설계대로 작동함을 실증** — load-test-auditor의 BLOCK(공유 디스패처 조기 완료 버그) → 감독자가 계약 직접 정정 → 재작업 → 재감사 APPROVE, 이어서 실제 `dotnet build`에서 코드 리뷰 3회가 놓친 `CS4007` 컴파일 에러까지 발견·수정.

## 원본 데이터

각 하네스의 전체 `_workspace` 스냅샷(findings JSON, 리포트, 생성 코드 등)은 `tests/results/{harness}/`에, 채점 원본은 각 `tests/results/{harness}/scorecard.json`에 보존돼 있습니다(`.gitignore`로 커밋 대상 아님, 로컬 증거용).
