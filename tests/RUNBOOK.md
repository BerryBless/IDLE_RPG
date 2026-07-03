# 하네스 테스트 스위트 RUNBOOK

6개 하네스(에이전트 21·스킬 24)를 2계층으로 검증한다: **계층1**(정적 구조, 결정적, CI 게이트)과
**계층2**(기능 픽스처, 비결정적, LLM 실행 필요). 설계 배경은 `plan/` 또는 세션 기록 참조.

## 계층1 — 정적 구조 검증 (아무 때나, 초 단위)

```powershell
# 빠른 게이트 (무빌드)
pwsh tests/layer1-static-check.ps1

# 권위 스위트 (구조 파싱 + _workspace 데이터흐름 그래프)
dotnet test tests/IdleRpg.HarnessTests/IdleRpg.HarnessTests.csproj
```

둘 다 exit 0이어야 한다. 하나라도 실패하면 하네스 정의(.claude/agents, .claude/skills)에
drift가 생긴 것 — 코드 수정 전에 먼저 고친다.

## 계층2 — 기능 픽스처 (하네스별 1개씩, LLM 실행 필요)

**원칙: 한 번에 하나의 하네스만 실행한다.** `_workspace/`가 6개 하네스에 공유되므로 동시 실행 시
산출물이 섞인다.

### 공통 절차 (분석형 3개: code-review, concurrency-guard, gc-guard)

1. **Wipe**: 저장소 루트의 `_workspace/`가 있으면 삭제(또는 이름 변경)해서 Phase 0가 "초기 실행"으로
   판단하게 한다.
2. **Invoke**: 해당 오케스트레이터 스킬을 픽스처 대상으로 트리거한다.
   - code-review: `/code-review-orchestrator tests/fixtures/code-review/Defective.cs`
   - gc-guard: "tests/fixtures/gc-guard/ 디렉토리 메모리 최적화 검사해줘" (경로 지정 케이스)
   - concurrency-guard: "tests/fixtures/concurrency-guard/ 동시성 검사해줘"
3. **Snapshot**: 완료 즉시 `_workspace/`를 결과 디렉토리로 복사한다(다음 하네스 실행 전에!):
   ```powershell
   Copy-Item -Recurse _workspace tests/results/<harness>
   ```
4. **Grade**:
   ```powershell
   dotnet run --project tests/IdleRpg.Grader -- tests/fixtures/<harness> tests/results/<harness>
   ```
   콘솔에 recall(재현율)과 `MISSED` 항목이, `tests/results/<harness>/scorecard.json`에 상세가 출력된다.

### pipeline (생성형)

1. Wipe `_workspace/`.
2. Invoke: "TCP 이진 프로토콜 서버 파이프라인 설계해줘" 하고, 브리프를 물으면
   `tests/fixtures/pipeline/design_brief.md` 내용을 붙여넣는다.
3. Snapshot → `tests/results/pipeline`.
4. Grade: `dotnet run --project tests/IdleRpg.Grader -- tests/fixtures/pipeline tests/results/pipeline`
   — `03_load_test_audit.md`에 `APPROVE`가 있는지, `04_pipeline_architecture.md`가 충분한 분량인지 확인.

### tdd (생성형)

1. Wipe `_workspace/`.
2. Invoke: "Calculator를 TDD로 구현해줘" 하고, 요구사항을 물으면
   `tests/fixtures/tdd/requirements.md` 내용을 붙여넣는다.
3. Snapshot → `tests/results/tdd`.
4. Grade — `03_qa/test_results.txt`에서 `Failed:\s*0` 매칭 여부(= dotnet test green)를 확인.

### commitandpush (생성형, 음성+양성 2회)

**주의**: 반드시 픽스처가 만든 임시 저장소 안에서만 `/commitandpush`를 실행한다. 절대 실제
IDLE_RPG 저장소 루트에서 픽스처 검증용으로 실행하지 않는다.

**음성(보안 차단) — 완전 자동화 가능:**
```powershell
$repo = & tests/fixtures/commitandpush/setup-secret-repo.ps1
# $repo 디렉토리로 이동해 그 안에서 /commitandpush 실행 → Phase 1에서 FAIL 즉시 중단돼야 함
# 완료 후: Copy-Item -Recurse "$repo/_workspace" tests/results/commitandpush-negative
Remove-Item -Recurse -Force $repo   # 정리
```
```powershell
dotnet run --project tests/IdleRpg.Grader -- tests/fixtures/commitandpush/negative tests/results/commitandpush-negative
```

**양성(정상 커밋) — 커밋 메시지 확인(y/n/edit) 프롬프트가 있어 반자동:**
```powershell
$repo = & tests/fixtures/commitandpush/setup-clean-repo.ps1
# $repo 디렉토리로 이동해 그 안에서 /commitandpush 실행 → 메시지 미리보기에서 y로 확인
# 완료 후: Copy-Item -Recurse "$repo/_workspace" tests/results/commitandpush-positive
Remove-Item -Recurse -Force $repo   # 정리 (원격 없는 임시 저장소이므로 안전)
```
```powershell
dotnet run --project tests/IdleRpg.Grader -- tests/fixtures/commitandpush/positive tests/results/commitandpush-positive
```

## 결과 해석

- `--strict` 플래그 없이 실행하면 채점기는 항상 exit 0이다(계층2는 비결정적이므로 CI를 막지 않음,
  report-only). 실제 통과 여부는 콘솔의 `Recall`/`PASS`/`FAIL` 표기와 `scorecard.json`으로 판단한다.
- 산출물(`_workspace`)이 아직 없으면 채점기는 `SKIP`을 출력하고 exit 0 — 실패가 아니라 "아직
  실행 안 됨" 신호다.
- recall이 100%가 아니어도 하네스가 "고장"은 아니다 — LLM 출력은 비결정적이므로 실행마다 달라질
  수 있다. 반복 실행해 추세를 보는 것이 한 번의 결과보다 의미 있다.

## 자기증명 (LLM 실행 없이 채점기/게이트 자체를 검증하는 방법)

- **계층1 자기증명**: `.claude`를 스크래치 디렉토리에 복사 → 의도적 결함(frontmatter 삭제,
  agent_type 오탈자, 참조 삭제로 고아 생성) 주입 → `pwsh tests/layer1-static-check.ps1 -RepoRoot <스크래치경로>`
  가 정확히 그 결함들을 잡아내는지 확인. 실제 `.claude`는 절대 건드리지 않는다.
- **계층2 자기증명**: `tests/fixtures/<harness>/expected.json`의 실제 스키마 필드명(`hot_path_allocs`,
  `risk_level`, `final_score` 등)에 맞춰 손수 `_workspace` 스냅샷을 합성 → 채점기가 100% recall을
  내는지, 항목을 일부 제거했을 때 정확히 그 항목만 `MISSED`로 나오는지 확인.
