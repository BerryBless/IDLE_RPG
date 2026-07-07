---
name: worklog
description: >
  완료된 작업 사이클을 한국어로 문서화하는 하네스. git 이력·변경 소스·관련 설계/리뷰 문서를
  읽어 개요·타임라인·변경 사항·3종 mermaid 다이어그램(클래스/시퀀스/순서도)·검증 결과·관련
  문서 링크·향후 과제를 담은 워크로그를 worklog/<키워드>_<MMDD>.md로 생성한다.
  '/worklog', '워크로그 작성', '작업 문서화', '작업 결과 정리', '이번 작업 기록해줘',
  '작업 로그 남겨줘' 요청 시 반드시 이 스킬을 사용할 것.
  후속 실행: '워크로그 다시', '워크로그 갱신', '다이어그램만 다시' 포함.
---

# worklog 하네스

완료된 작업 사이클(기능 구현, 코드리뷰, 버그 수정 등)을 `worklog-writer` 에이전트에게 위임해
한국어 워크로그 문서 1개(3종 mermaid 다이어그램 포함)로 남기는 오케스트레이터.

## 실행 모드: 단일 에이전트 디스패치

`code-review-orchestrator`(4개 도메인 병렬 팬아웃)와 달리, 이 하네스는 문서 작성이라는 단일
관점의 작업이라 에이전트 1명만 디스패치한다. `commitandpush`처럼 `Agent` 툴로 직접 호출한다
(`TeamCreate` 등 팀 코디네이션 툴은 이 저장소 세션에서 실제로 쓸 수 없으므로 전제하지 않는다).

---

## Stop 훅과의 커밋 경합 방지 (락 프로토콜)

`commitandpush`와 동일한 이유([2026-07-05 사고] 참고, `.claude/skills/commitandpush/SKILL.md`)로
`.git/commitandpush.lock`을 재사용한다 — 이 하네스도 에이전트 디스패치로 턴이 길어질 수 있어
Stop 훅이 워크로그 작성 도중에 끼어들 위험이 있다.

- **Phase 0 시작 시 락 생성:** `touch .git/commitandpush.lock`
- **각 Phase 시작 시 락 갱신(mtime 터치):** 동일 명령 재실행
- **종료 시(성공/실패/중단 모든 경로) 락 해제:** `rm -f .git/commitandpush.lock`
- **안전망:** 해제를 놓쳐도 15분 후 만료로 간주되어 `auto-commit.ps1`이 자동 커밋을 재개한다.
- Phase 0 직후 남아있는 `.git/auto_commit_msg.txt`가 있으면 삭제한다 — 이전 턴의 낡은 메시지가
  이번 워크로그 커밋에 잘못 붙는 것을 방지.

## 절대 금지 규칙

이 스킬(과 `worklog-writer` 에이전트)은 아래를 어떤 이유로도 실행하지 않는다:

| 금지 명령 | 사유 |
|----------|------|
| `git commit` (직접 호출) | 커밋은 `.git/auto_commit_msg.txt` + Stop 훅에 위임한다 — 브랜치 자동 라우팅·보안 검사·잠금 정책을 중복 구현하지 않기 위함 |
| `git push`, `git config *`, `git reset --hard`, `git clean -fd` | 이 스킬의 책임 범위 밖 — Stop 훅이 이미 처리한다 |

---

## Phase 0: 컨텍스트 확인

```bash
touch .git/commitandpush.lock
rm -f .git/auto_commit_msg.txt
```

이미 `worklog/`에 오늘 날짜로 같은 키워드의 파일이 있고 사용자가 "다시"/"갱신"을 요청했다면
같은 경로를 덮어쓰는 모드로 진행한다(새 파일로 취급하지 않음).

---

## Phase 1: 문서화 대상 결정

**케이스 A — 인수 없음:**
```bash
git log --oneline -20
```
결과를 바탕으로 "가장 최근에 완결된 작업 사이클"의 커밋 범위를 추정해 사용자에게 확인받는다
(예: "설계~구현~리뷰 수정까지 이어지는 최근 커밋들을 문서화할까요? 범위: `<base>..<head>`").

**케이스 B — 키워드 지정** (예: "다중 플레이어 샤딩 문서화해줘"):
```bash
git log --oneline --all --grep="<키워드 관련어>" -i
```
로 관련 커밋을 찾아 범위를 확정한다. 출력 파일명은 `worklog/<키워드>_<MMDD>.md`.

**케이스 C — 커밋 범위 직접 지정** (예: `/worklog 299b4b1..7eb5b66`):
범위를 그대로 사용한다.

**관련 문서 자동 수집 (best-effort):**
```bash
git show --stat <범위> -- docs/superpowers/specs docs/superpowers/plans docs/code-reviews
```
범위 내 커밋이 건드린 설계/계획/리뷰 문서가 있으면 경로를 수집한다. 못 찾아도 진행— 에이전트가
필요하면 자체적으로 `git log`/`ls`로 탐색할 수 있다.

**중단 조건:** 범위가 비어 있으면(`git log <범위>`가 아무것도 반환하지 않으면) 사용자에게
알리고 락을 해제한 뒤 중단한다.

---

## Phase 2: worklog-writer 디스패치

**시작 시 락 갱신:** `touch .git/commitandpush.lock`

```
Agent(
  description: "워크로그 작성: <키워드>",
  subagent_type: "general-purpose",
  model: "opus",
  run_in_background: false,
  prompt: """
    worklog-writer.md(.claude/agents/worklog-writer.md) 에이전트 역할을 수행하라.

    커밋 범위: <범위>
    출력 경로: worklog/<키워드>_<MMDD>.md
    관련 문서: <Phase 1에서 수집한 경로 목록, 없으면 "없음">
    작업 디렉토리: <project_root>

    9개 섹션(개요/타임라인/변경 사항 요약/classDiagram/sequenceDiagram/flowchart/검증 결과/
    관련 문서 링크/향후 과제)을 모두 포함한 한국어 워크로그를 위 출력 경로에 작성하라.
    다이어그램은 실제 변경된 소스 코드를 직접 Read해서 그리고(계획 문서를 베끼지 말 것),
    mermaid 줄바꿈은 반드시 <br/>를 써라.
  """
)
```

`run_in_background: false`로 foreground 실행해 턴 노출을 최소화한다(commitandpush와 동일 원칙).

---

## Phase 3: 저장 검증 + CLAUDE.md 갱신

1. 출력 경로에 파일이 실제로 생성됐는지 확인
2. 9개 섹션 헤더가 모두 있는지, `classDiagram`/`sequenceDiagram`/`flowchart` 3종 코드블록이
   모두 있는지 확인
3. 파일 내 mermaid 블록에 리터럴 `\n`이 남아있지 않은지 확인(있으면 `<br/>`로 직접 고친다)
4. `CLAUDE.md`의 "현재 워크로그 목록" 표에 새 행을 추가한다: `[<키워드>_<MMDD>.md](worklog/<키워드>_<MMDD>.md) | <한 줄 요약>`

---

## Phase 4: 커밋 위임

**락 해제 직전, 커밋 메시지 작성:**

```bash
cat > .git/auto_commit_msg.txt << 'EOF'
문서: <키워드> 워크로그 추가

<무엇을 문서화했는지, 왜 필요했는지 1~2문장 WHY 중심 요약>

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
rm -f .git/commitandpush.lock
```

직접 `git commit`을 호출하지 않는다 — 턴이 끝나면 기존 Stop 훅(`scripts/auto-commit.ps1`)이
이 메시지를 소비해 커밋하고, 현재 브랜치가 `master`/`main`이면 `claude` 브랜치로 자동
전환한 뒤 커밋·푸시한다(기존 정책, 이 스킬이 재구현하지 않음).

---

## 에러 핸들링

| 상황 | 처리 |
|------|------|
| 범위가 비어 있음 | Phase 1에서 사용자에게 알리고 락 해제 후 중단 |
| `worklog-writer` 실패/타임아웃 | 1회 재시도. 재실패 시 사용자에게 알리고 락 해제 후 중단(부분 산출물은 남겨둠) |
| 9개 섹션 중 일부 누락 또는 mermaid 3종 중 일부 누락 | `worklog-writer`를 같은 입력으로 1회 재디스패치. 재실패 시 무엇이 빠졌는지 사용자에게 보고 |
| mermaid 렌더 오류(리터럴 `\n` 발견 등) | Phase 3에서 직접 `<br/>`로 고쳐 저장(재디스패치 불필요) |
| git 저장소 아님 | 즉시 중단, 안내 |

---

## 테스트 시나리오 (정상 흐름)

1. 사용자: `/worklog 299b4b1..7eb5b66`
2. Phase 0: 락 생성, 낡은 메시지 정리
3. Phase 1: 케이스 C(범위 직접 지정), 관련 문서 3개(design spec/plan/code-review) 자동 수집
4. Phase 2: `worklog-writer` 디스패치 → `worklog/battle_sharding_0707.md` 생성
5. Phase 3: 9섹션·3종 mermaid·`<br/>` 확인, CLAUDE.md 표에 1행 추가
6. Phase 4: 커밋 메시지 작성, 락 해제 → 턴 종료 시 Stop 훅이 커밋(claude 브랜치로 라우팅) & 푸시
