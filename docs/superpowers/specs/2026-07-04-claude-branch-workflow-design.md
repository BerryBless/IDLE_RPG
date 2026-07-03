# Claude 커밋의 claude 브랜치 자동 라우팅 — 설계 문서

## 배경 및 목적

현재 IDLE_RPG는 두 개의 독립적인 경로로 git에 커밋·푸시된다:

1. **Stop 훅 자동 커밋** (`scripts/auto-commit.ps1`) — 매 턴 종료 시 무조건 현재 체크아웃된 브랜치에 직접 커밋·푸시한다. 브랜치 보호 로직이 전혀 없다.
2. **`/commitandpush` 스킬** (`.claude/agents/git-push-controller.md`) — main/master 직접 push 시 "경고 후 사용자 명시적 확인 요구"라는 로직은 있으나, 실제로 별도 브랜치를 만들지는 않는다.

두 경로 모두 결과적으로 Claude가 만든 모든 커밋이 `master`에 그대로 쌓인다. 사용자는 Claude의 커밋과 자신의 커밋을 분리해 리뷰할 방법이 없고, `master`가 항상 Claude의 최신 자동 커밋으로 오염된다.

**목적:** Claude가 생성하는 모든 커밋을 `master`가 아닌 전용 `claude` 브랜치로 자동 라우팅해, `master`는 사용자가 명시적으로 병합하기 전까지 깨끗하게 유지되도록 한다.

## 설계 결정

브레인스토밍 과정에서 다음 4가지를 확정했다(각 항목 표는 채택안과 후보 비교):

| 결정 사항 | 채택안 | 후보(기각) | 사유 |
|---|---|---|---|
| 적용 범위 | Stop 훅 + `/commitandpush` 둘 다 | 둘 중 하나만 | Claude가 만드는 모든 커밋이 동일 규칙을 따라야 일관성 있음. 하나만 적용하면 경로별로 동작이 달라 혼란 |
| 브랜치 운영 방식 | 단일 지속 브랜치(`claude`) | 세션/날짜별 새 브랜치(`claude/2026-07-04`) | 관리 부담 최소화. 날짜별 분기는 히스토리는 깔끔해지지만 브랜치가 무한정 늘어나고 병합 시 추적 비용이 커짐 |
| master 반영 | 사용자가 직접 머지(수동) | Claude가 `gh pr create`로 자동 PR | 가장 안전하고 리뷰 부담이 명확함. 자동 PR은 `gh` CLI 설치·인증 의존성이 추가되고, 병합 여부 판단을 Claude에게 위임하는 셈이 되어 과도함 |
| 전환 후 작업트리 상태 | `claude` 브랜치에 계속 머무름 | 커밋·푸시 직후 원래 브랜치(master)로 복귀 | master로 복귀하면 방금 커밋한 파일이 작업트리에서 사라져(master엔 없으므로) 사용자가 혼란을 겪음. 계속 머무르는 편이 세션 연속성에 자연스러움 |
| 전환 트리거 조건 | 현재 브랜치가 master/main일 때만 전환 | 현재 브랜치와 무관하게 항상 claude로 전환 | 사용자가 의도적으로 체크아웃한 feature 브랜치까지 가로채면 사용자의 명시적 작업 컨텍스트를 파괴함. 보호 대상은 master/main뿐 |

## 컴포넌트 구조

```
IDLE_RPG/
├── scripts/
│   └── auto-commit.ps1              ← 브랜치 확인·전환 로직 삽입 (스테이징 전)
├── .claude/
│   ├── agents/
│   │   └── git-push-controller.md   ← "브랜치 권한 확인" 섹션을 자동 전환 방식으로 교체
│   └── skills/
│       └── commitandpush/
│           └── SKILL.md             ← Phase 3 프롬프트에 새 동작 반영
└── CLAUDE.md                        ← Git 하네스 변경 이력에 기록
```

의존 관계: 두 실행 경로(Stop 훅, `/commitandpush`)는 서로 호출하지 않는 독립 스크립트/에이전트이므로, **동일한 브랜치 전환 로직을 각자의 언어(PowerShell / 마크다운 지시문)로 중복 구현**한다. 공유 스크립트로 추출하는 것도 가능하지만, 현재 두 경로의 실행 환경이 다르므로(하나는 PowerShell 프로세스, 하나는 에이전트가 Bash 도구로 git 명령을 직접 실행) 억지로 통합하면 오히려 복잡도가 늘어난다. 로직 자체는 아래처럼 동일한 3단계 판단으로 통일한다.

## 핵심 브랜치 전환 로직 (양쪽 공통 원리)

```
IF 현재 브랜치 IN {master, main}:
    IF 로컬에 claude 브랜치 존재:
        git checkout claude
    ELSE IF 원격(origin/claude)에 존재:
        git checkout -b claude origin/claude
    ELSE:
        git checkout -b claude          # 현재 master HEAD에서 분기
    IF 전환 실패 (충돌 등):
        전환 포기, 원래 브랜치에 커밋 진행, 경고만 기록 (자동 병합/리베이스 시도 안 함)
ELSE:
    아무 것도 하지 않음 (사용자가 체크아웃한 브랜치 그대로 사용)

... 커밋 실행 ...

git push -u origin <현재_브랜치>   # claude든 아니든 -u로 안전하게 upstream 설정
```

### `scripts/auto-commit.ps1` 변경 지점

기존 스크립트의 "3. 전체 스테이지" 단계(현재 26번째 줄) **직전**에 브랜치 확인·전환 블록을 삽입한다.

- `git branch --show-current`로 현재 브랜치 확인
- `git show-ref --verify --quiet refs/heads/claude`로 로컬 존재 여부 판정(exit code)
- `git ls-remote --heads origin claude`로 원격 존재 여부 판정(fetch 불필요, 원격 직접 조회)
- 전환 명령 실패 시(`$LASTEXITCODE -ne 0`) 경고만 출력하고 원래 브랜치에서 계속 진행 — 자동 훅이므로 사용자 개입 없이 항상 완주해야 함
- 기존 8번째 줄의 `git -C $repo push` 를 브랜치 인지형으로 교체: `git -C $repo push -u origin $branch`

### `.claude/agents/git-push-controller.md` 변경 지점

"### 3. 브랜치 권한 확인" 섹션(현재 39~46줄)을 다음 내용으로 교체:

- 섹션 제목을 "### 3. 브랜치 확인 및 claude 브랜치 전환"으로 변경
- 기존의 "main/master 직접 push → 경고 후 사용자 확인 요구"를 제거하고, 위 공통 로직(로컬 존재/원격 존재/신규 분기 3분기)으로 대체
- "보호 브랜치 감지 → push 중단, PR 생성 안내"도 제거 — 이제 중단 대신 자동 전환하므로 해당 문구는 더 이상 유효하지 않음
- 이 섹션은 반드시 "### 4. 커밋 실행"보다 먼저 와야 한다(커밋이 claude 브랜치 위에서 일어나야 하므로) — 현재도 순서가 3→4라 섹션 순서 자체는 유지, 내용만 교체
- "## 입력/출력 프로토콜"의 출력 템플릿(현재 82~95줄)에 전환 여부를 알리는 줄 추가: `**브랜치 전환:** master → claude (자동)` 또는 `**브랜치 전환:** 없음 (이미 claude/기타 브랜치)`
- "## 에러 핸들링" 표에 "claude 브랜치 전환 실패(충돌)" 행 추가: 처리 = "전환 포기, 현재 브랜치에 커밋 후 사용자에게 수동 정리 안내"

### `.claude/skills/commitandpush/SKILL.md` 변경 지점

"## Phase 3: 커밋 & 푸시 (git-push-controller)" 섹션의 프롬프트 텍스트(137~157줄) 중, 147번째 줄
"3. 현재 브랜치 확인 및 보호 브랜치 여부 체크"를
"3. 현재 브랜치 확인, master/main이면 claude 브랜치로 자동 전환(git-push-controller.md 로직 참조) 후 진행"으로 수정한다.

### `CLAUDE.md` 변경 지점

"## 하네스: Git 자동 커밋 & 푸시 (Git Automator)" 섹션의 변경 이력 테이블에 한 줄 추가:
| 2026-07-04 | Claude 커밋을 master 대신 claude 브랜치로 자동 라우팅 | scripts/auto-commit.ps1, agents/git-push-controller.md, skills/commitandpush/SKILL.md | master를 Claude 자동 커밋으로부터 보호, 사용자가 명시적으로 병합하기 전까지 깨끗하게 유지 |

## 빌드 검증

이 변경은 코드가 아니라 스크립트/에이전트 정의/문서이므로 `dotnet build`는 무관하다. 대신:

1. **PowerShell 구문 검증**: `pwsh -NoProfile -Command "& { . 'scripts/auto-commit.ps1' -WhatIf }"` 또는 `Get-Content scripts/auto-commit.ps1 | Out-Null`로 파싱 오류만 우선 확인(실제 실행은 격리된 테스트 저장소에서).
2. **격리된 임시 저장소로 엔드투엔드 시나리오 검증** (이번 세션에서 `tests/fixtures/commitandpush/setup-*.ps1`로 이미 검증한 방식과 동일한 패턴):
   - 시나리오 A: master에 있는 임시 저장소 → 훅 실행 → `claude` 브랜치가 새로 생성되고 그 위에 커밋됐는지, master는 그대로인지 확인
   - 시나리오 B: 이미 `claude` 브랜치가 있는 임시 저장소(master보다 앞서 있음) → 훅 실행 → 기존 `claude`로 전환해 이어서 커밋되는지 확인
   - 시나리오 C: `feature/x` 브랜치에 있는 임시 저장소 → 훅 실행 → 전환 없이 `feature/x`에 그대로 커밋되는지 확인
   - 시나리오 D(선택, 저위험): `claude` 브랜치가 원격에만 있고 로컬에 없는 저장소 → 훅 실행 → `origin/claude`를 추적하는 로컬 `claude`가 생성되는지 확인
3. **실제 IDLE_RPG 저장소에서 최종 확인**: 다음 정상적인 세션 종료 시 Stop 훅이 실제로 `master`가 아닌 `claude` 브랜치를 만들어 커밋하는지 `git branch --show-current`와 `git log`로 확인.

## 향후 확장 포인트

- 사용자가 원하면 다음 사이클에서 `gh pr create` 자동 PR 생성 옵션을 추가할 수 있다(현재는 의도적으로 배제).
- `claude` 브랜치가 `master`보다 많이 뒤처지는 경우(사용자가 master에 직접 여러 커밋을 쌓은 경우) 자동 rebase/merge 안내를 추가할 수 있다(현재는 의도적으로 자동 해결 시도 안 함 — 충돌 시 사용자에게 위임).
