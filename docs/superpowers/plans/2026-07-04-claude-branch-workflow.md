# Claude 브랜치 자동 라우팅 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Claude가 만드는 모든 커밋(Stop 훅 자동 커밋 + `/commitandpush` 스킬)이 `master`/`main`을 직접 건드리지 않고, 전용 `claude` 브랜치로 자동 라우팅되게 한다.

**Architecture:** 커밋 직전에 현재 브랜치가 `master`/`main`인지 확인하는 게이트를 두 실행 경로(PowerShell 훅, 에이전트 지시문)에 동일한 3분기 로직으로 각각 구현한다 — 로컬에 `claude` 있으면 checkout, 없고 원격에 있으면 추적 checkout, 둘 다 없으면 현재 HEAD에서 신규 분기. 전환 후에는 `claude` 브랜치에 계속 머무른다. 전환 실패 시 원래 브랜치에 그대로 커밋해 자동 훅이 절대 멈추지 않게 한다.

**Tech Stack:** PowerShell 7(pwsh), git CLI, Markdown 에이전트/스킬 정의(Claude Code 하네스 규약).

## Global Constraints

- 전환 대상 브랜치 이름은 정확히 `claude` 하나(단일 지속 브랜치, 세션/날짜별 분기 없음).
- 전환 트리거 조건은 현재 브랜치가 정확히 `master` 또는 `main`일 때만(그 외 브랜치는 절대 건드리지 않음).
- `master` → `claude` 자동 병합/PR 생성 없음 — 사용자가 GitHub에서 수동으로만 병합.
- 기존 절대 금지 명령 목록(`git config *` 변경, `git reset --hard`, `git clean -fd`, `git push --force`/`-f`, `-i` 포함 인터랙티브 명령, 무조건부 `git commit --amend`)은 이 변경 이후에도 그대로 유지.
- 자동 훅(`scripts/auto-commit.ps1`)은 브랜치 전환이 실패해도 절대 멈추지 않고 원래 브랜치에 커밋을 완료해야 한다(무중단 우선).
- push는 항상 `-u`(upstream 설정) 플래그를 포함해 최초 푸시든 반복 푸시든 안전하게 동작해야 한다.

---

### Task 1: `scripts/auto-commit.ps1`에 브랜치 자동 전환 로직 추가

**Files:**
- Modify: `E:\project\IDLE_RPG\scripts\auto-commit.ps1` (전체, 58줄 → 약 78줄)

**Interfaces:**
- Consumes: 없음 (독립 스크립트, Stop 훅에서 인자 없이 호출됨)
- Produces: 없음 (부수효과만 — git 브랜치 전환·커밋·푸시)

- [ ] **Step 1: 격리된 시나리오 A로 "전환 전" 동작 확인 (현재 스크립트가 master를 오염시킴을 재현)**

```powershell
$scenarioA = Join-Path $env:TEMP ("autocommit-scenA-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scenarioA | Out-Null
New-Item -ItemType Directory -Path (Join-Path $scenarioA 'scripts') | Out-Null
Push-Location $scenarioA
git init -q -b master
git config user.name 'plan-test-bot'
git config user.email 'plan-test@local.test'
'init' | Set-Content 'README.md'
git add README.md
git commit -q -m 'init'
Copy-Item 'E:\project\IDLE_RPG\scripts\auto-commit.ps1' 'scripts\auto-commit.ps1'
'change' | Add-Content 'README.md'
pwsh -File 'scripts\auto-commit.ps1'
git branch --show-current
Pop-Location
```

Run: 위 블록 그대로 실행
Expected: 마지막 `git branch --show-current` 출력이 `master` (수정 전 스크립트는 브랜치를 바꾸지 않으므로 — 이 결과가 "고쳐야 할 문제"를 재현한 것). `$scenarioA` 경로는 다음 스텝에서 재사용하니 기록해 둔다.

- [ ] **Step 2: `scripts/auto-commit.ps1` 전체를 아래 내용으로 교체**

```powershell
# auto-commit.ps1
# Stop 훅에서 호출 — 메인 세션이 .git/auto_commit_msg.txt 에 남긴 WHY 커밋 메시지를
# 읽어서 커밋 & 푸시한다. 파일이 없거나 형식이 맞지 않으면 접두사 기반 폴백 메시지 사용.
# (이전 방식: Stop 훅이 claude -p 를 직접 호출 → 콜드스타트/stdin 취약성으로 폴백 빈발)

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# $PSScriptRoot: 스크립트 자신의 디렉토리(<repo>/scripts/)를 런타임에 알려주므로
# 리포 루트를 하드코딩 없이 부모 디렉토리로 유도 — 다른 프로젝트로 이식 시 경로 수정 불필요
$repo = Split-Path $PSScriptRoot -Parent

# 1. 변경사항 확인
$status = git -C $repo status --porcelain 2>&1
if (-not $status) { exit 0 }

# 2. 보안 검사 — 민감 파일 감지 시 차단
$statusStr = ($status | Where-Object { $_ }) -join ' '
$sensitiveHit = ($statusStr -match '(?i)\.(env|pem|key|p12|pfx|jks|ppk)(\s|$)|id_rsa|id_ed25519|credentials\.json|secrets\.json') `
                -and ($statusStr -notmatch '\.example')
if ($sensitiveHit) {
    Write-Output '{"systemMessage":"⚠️ 민감 파일 감지 — 자동 커밋 차단. /commitandpush 로 수동 처리 필요."}'
    exit 2
}

# 3. 브랜치 확인 — master/main이면 claude 브랜치로 자동 전환
#    Claude가 만드는 커밋이 master를 직접 오염시키지 않도록, 보호 대상 브랜치일 때만
#    claude 브랜치(로컬/원격 존재 여부에 따라 checkout 또는 신규 분기)로 전환한 뒤 계속 진행한다.
#    전환 실패(작업트리 충돌 등) 시에는 자동 훅이므로 사용자 개입 없이 항상 완주해야 하며,
#    전환을 포기하고 원래 브랜치에 그대로 커밋한다(무중단 우선, 데이터 손실 없음).
$branch = (git -C $repo branch --show-current 2>&1).Trim()
if ($branch -eq 'master' -or $branch -eq 'main') {
    git -C $repo show-ref --verify --quiet refs/heads/claude
    $hasLocalClaude = ($LASTEXITCODE -eq 0)

    if ($hasLocalClaude) {
        git -C $repo checkout claude 2>&1 | Out-Null
    }
    else {
        $remoteClaude = git -C $repo ls-remote --heads origin claude 2>&1
        if ($remoteClaude) {
            git -C $repo checkout -b claude origin/claude 2>&1 | Out-Null
        }
        else {
            git -C $repo checkout -b claude 2>&1 | Out-Null
        }
    }

    if ($LASTEXITCODE -eq 0) {
        $branch = 'claude'
    }
    else {
        # 전환 실패 — 원래 브랜치($branch, 여전히 master/main)에서 계속 진행
        Write-Output "::warning:: claude 브랜치 전환 실패, $branch 브랜치에 그대로 커밋합니다"
    }
}

# 4. 전체 스테이지
git -C $repo add . 2>&1 | Out-Null

# 5. 스테이지된 파일 확인
$staged = @(git -C $repo diff --staged --name-only 2>&1 | Where-Object { $_ -ne '' })
if ($staged.Count -eq 0) { exit 0 }

# 6. 메인 세션이 남긴 WHY 메시지 파일 읽기 (consume-once)
#    메인 Claude 가 턴 종료 직전에 .git/auto_commit_msg.txt 를 작성해두면
#    훅이 그 메시지를 사용하고 파일을 삭제한다. nested claude 세션 없이 즉시 완료.
$msgFile = Join-Path $repo ".git\auto_commit_msg.txt"
$commitMsg = ""
if (Test-Path $msgFile) {
    $commitMsg = (Get-Content $msgFile -Raw -Encoding UTF8).Trim()
    Remove-Item $msgFile -Force 2>$null   # 재사용 방지: 읽는 즉시 삭제
}

# 7. 메시지 없거나 형식 불량 시 접두사 기반 폴백
if (-not $commitMsg -or $commitMsg -notmatch '^(추가|수정|버그수정|리팩토링|문서|테스트|의존성):') {
    $added = @(git -C $repo diff --staged --name-only --diff-filter=A 2>&1 | Where-Object { $_ })
    $prefix = if ($added.Count -eq $staged.Count) { "추가" } else { "수정" }
    $first  = [System.IO.Path]::GetFileNameWithoutExtension($staged[0])
    $commitMsg = "${prefix}: $first 외 $($staged.Count - 1)개 파일 변경`n`nCo-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>"
}

# 8. 커밋
git -C $repo commit -m $commitMsg 2>&1

# 9. 원격 저장소가 있으면 푸시 (-u로 upstream 미설정 상태에도 안전하게 처리)
if ($LASTEXITCODE -eq 0) {
    $remote = git -C $repo remote 2>&1
    if ($remote) { git -C $repo push -u origin $branch 2>&1 | Out-Null }
}
```

- [ ] **Step 3: 시나리오 A를 새로 만들어 "전환 후" 동작 확인 (원격 없는 순수 로컬 검증)**

```powershell
$scenarioA2 = Join-Path $env:TEMP ("autocommit-scenA2-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scenarioA2 | Out-Null
New-Item -ItemType Directory -Path (Join-Path $scenarioA2 'scripts') | Out-Null
Push-Location $scenarioA2
git init -q -b master
git config user.name 'plan-test-bot'
git config user.email 'plan-test@local.test'
'init' | Set-Content 'README.md'
git add README.md
git commit -q -m 'init'
Copy-Item 'E:\project\IDLE_RPG\scripts\auto-commit.ps1' 'scripts\auto-commit.ps1'
'change' | Add-Content 'README.md'
pwsh -File 'scripts\auto-commit.ps1'
Write-Host "current branch: $(git branch --show-current)"
Write-Host "master log:"
git log master --oneline
Write-Host "claude log:"
git log claude --oneline
Pop-Location
```

Run: 위 블록 그대로 실행
Expected:
- `current branch: claude`
- `master log:`에는 `init` 커밋 1개만 (README 변경이 master에 없음)
- `claude log:`에는 `init` + README 변경 커밋 2개

- [ ] **Step 4: 시나리오 정리**

```powershell
Remove-Item -Recurse -Force $scenarioA
Remove-Item -Recurse -Force $scenarioA2
```

Run: 위 두 줄 실행
Expected: 오류 없이 종료 (두 임시 디렉토리가 삭제됨)

- [ ] **Step 5: Commit**

```bash
git add scripts/auto-commit.ps1
git commit -m "$(cat <<'EOF'
수정: Claude 자동 커밋을 master 대신 claude 브랜치로 라우팅

- Stop 훅 자동 커밋이 master/main에 있을 때만 claude 브랜치를 만들거나 전환한 뒤 그 위에 커밋·푸시하도록 변경
- 전환 실패 시에도 원래 브랜치에 커밋을 완료해 자동 훅이 절대 멈추지 않게 함
- push에 -u 플래그를 추가해 claude 브랜치 최초 푸시도 안전하게 처리

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

주의: 이 커밋 자체는 지금 실행 중인 세션이 `master`에서 만드는 것이므로, **이 Task를 완료하고 세션이 종료될 때 Stop 훅이 방금 수정한 새 스크립트로 자기 자신의 커밋을 처리하게 된다** — `master`에서 `claude`로 자동 전환되는 첫 실전 사례가 된다. 이는 의도된 동작이며 별도 배포 단계가 필요 없다.

---

### Task 2: 로컬 `claude` 브랜치 기존 존재 + 다른 브랜치 무시 시나리오 검증

**Files:**
- 없음 (Task 1에서 이미 수정된 `scripts/auto-commit.ps1`을 대상으로 순수 검증만 수행)

**Interfaces:**
- Consumes: Task 1에서 완성된 `E:\project\IDLE_RPG\scripts\auto-commit.ps1`
- Produces: 없음 (검증 전용, 코드 변경 없음)

- [ ] **Step 1: 시나리오 B(로컬 claude 브랜치가 이미 존재하고 master보다 앞서 있음) 실행**

```powershell
$scenarioB = Join-Path $env:TEMP ("autocommit-scenB-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scenarioB | Out-Null
New-Item -ItemType Directory -Path (Join-Path $scenarioB 'scripts') | Out-Null
Push-Location $scenarioB
git init -q -b master
git config user.name 'plan-test-bot'
git config user.email 'plan-test@local.test'
'init' | Set-Content 'README.md'
git add README.md
git commit -q -m 'init'
git checkout -b claude
'from claude branch' | Add-Content 'README.md'
git add README.md
git commit -q -m 'existing claude commit'
git checkout master
Copy-Item 'E:\project\IDLE_RPG\scripts\auto-commit.ps1' 'scripts\auto-commit.ps1'
'second change' | Add-Content 'README.md'
pwsh -File 'scripts\auto-commit.ps1'
Write-Host "current branch: $(git branch --show-current)"
git log claude --oneline
Pop-Location
```

Run: 위 블록 그대로 실행
Expected:
- `current branch: claude`
- `git log claude --oneline`에 커밋 3개 (`init`, `existing claude commit`, 방금 새로 만든 `second change` 커밋) — 기존 claude 브랜치 위에 이어서 커밋됐음을 확인

- [ ] **Step 2: 시나리오 C(현재 master가 아닌 feature 브랜치)가 그대로 유지되는지 확인**

```powershell
$scenarioC = Join-Path $env:TEMP ("autocommit-scenC-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scenarioC | Out-Null
New-Item -ItemType Directory -Path (Join-Path $scenarioC 'scripts') | Out-Null
Push-Location $scenarioC
git init -q -b master
git config user.name 'plan-test-bot'
git config user.email 'plan-test@local.test'
'init' | Set-Content 'README.md'
git add README.md
git commit -q -m 'init'
git checkout -b feature/x
Copy-Item 'E:\project\IDLE_RPG\scripts\auto-commit.ps1' 'scripts\auto-commit.ps1'
'feature change' | Add-Content 'README.md'
pwsh -File 'scripts\auto-commit.ps1'
Write-Host "current branch: $(git branch --show-current)"
Pop-Location
```

Run: 위 블록 그대로 실행
Expected: `current branch: feature/x` (claude로 전환되지 않고 사용자가 체크아웃한 브랜치 그대로 커밋됨)

- [ ] **Step 3: 시나리오 정리**

```powershell
Remove-Item -Recurse -Force $scenarioB
Remove-Item -Recurse -Force $scenarioC
```

Run: 위 두 줄 실행
Expected: 오류 없이 종료

---

### Task 3: `.claude/agents/git-push-controller.md` 브랜치 전환 로직 반영

**Files:**
- Modify: `E:\project\IDLE_RPG\.claude\agents\git-push-controller.md:39-46` (섹션 3 전체 교체)
- Modify: `E:\project\IDLE_RPG\.claude\agents\git-push-controller.md:82-95` (출력 템플릿에 전환 여부 필드 추가)
- Modify: `E:\project\IDLE_RPG\.claude\agents\git-push-controller.md:97-105` (에러 핸들링 표에 행 추가)

**Interfaces:**
- Consumes: 없음 (에이전트 정의 파일, 자연어 지시문)
- Produces: `git-push-controller` 에이전트가 이 파일을 읽고 따르는 행동 규약 — Task 6(라이브 검증)이 이 산출물을 사용

- [ ] **Step 1: 39~46번째 줄("### 3. 브랜치 권한 확인" 섹션 전체)을 아래로 교체**

기존:
```markdown
### 3. 브랜치 권한 확인
```bash
git branch --show-current
git log --oneline origin/{현재 브랜치}..HEAD 2>/dev/null
```
- `main`/`master` 브랜치 직접 push → **경고 후 사용자 명시적 확인 요구**
- 보호 브랜치 감지 → push 중단, PR 생성 안내
```

교체 후:
```markdown
### 3. 브랜치 확인 및 claude 브랜치 전환
```bash
git branch --show-current
```
- 현재 브랜치가 `master`/`main`이 **아니면** → 그대로 진행 (사용자가 의도적으로 체크아웃한 브랜치로 간주, 건드리지 않음)
- 현재 브랜치가 `master`/`main`이면 → **claude 브랜치로 자동 전환한 뒤 그 위에서 커밋을 진행**:
  1. 로컬에 `claude` 브랜치가 있으면: `git checkout claude`
  2. 없고 원격(`origin/claude`)에 있으면: `git checkout -b claude origin/claude`
  3. 둘 다 없으면: `git checkout -b claude` (현재 master/main HEAD에서 새로 분기)
  4. 전환 명령이 실패하면(작업트리 충돌 등) → 전환을 포기하고 원래 브랜치(`master`/`main`)에 그대로 커밋 진행, 사용자에게 전환 실패 사실을 결과 보고에 명시
- 전환 이후 작업 디렉토리는 `claude` 브랜치에 계속 머무른다 — 다음 실행도 자연스럽게 같은 브랜치에 이어서 커밋된다.
- `master`/`main`으로의 병합은 Claude가 자동으로 수행하지 않는다. 사용자가 GitHub에서 직접 PR/머지로 처리한다.
```

- [ ] **Step 2: 82~95번째 줄(출력 템플릿)에 브랜치 전환 여부 필드 추가**

기존:
```markdown
**출력:** `_workspace/03_push_result.md`
```markdown
# 커밋 & 푸시 결과

**커밋 해시:** abc1234
**브랜치:** feature/packet-serialization
**원격:** origin/feature/packet-serialization
**상태:** 성공 | 실패 | 로컬만 완료

## 커밋 메시지
(적용된 메시지 전문)

## 주의 사항
(있으면 기재)
```
```

교체 후:
```markdown
**출력:** `_workspace/03_push_result.md`
```markdown
# 커밋 & 푸시 결과

**커밋 해시:** abc1234
**브랜치:** claude
**브랜치 전환:** master → claude (자동) | 없음 (이미 claude/기타 브랜치) | 실패 — master에 그대로 커밋됨
**원격:** origin/claude
**상태:** 성공 | 실패 | 로컬만 완료

## 커밋 메시지
(적용된 메시지 전문)

## 주의 사항
(있으면 기재)
```
```

- [ ] **Step 3: 97~105번째 줄(에러 핸들링 표)에 행 추가**

기존 표 마지막 행(`pre-commit hook 실패 | amend 조건 검사 후 안전한 경우만 처리`) 바로 아래에 다음 행 추가:
```markdown
| claude 브랜치 전환 실패(충돌 등) | 전환 포기, 원래 브랜치(master/main)에 그대로 커밋 진행, 결과 보고서에 전환 실패 사실과 수동 정리 방법(예: `git stash` 후 재시도) 안내 |
```

- [ ] **Step 4: 편집 결과를 직접 읽어 세 군데 수정이 모두 반영됐는지 확인**

Run: `Grep`으로 확인 — 패턴 `claude 브랜치로 자동 전환`이 39번째 줄 근방에, `브랜치 전환:`이 출력 템플릿에, `claude 브랜치 전환 실패`가 에러 표에 각각 존재하는지 확인
Expected: 3곳 모두 매칭 1건씩 발견

- [ ] **Step 5: Commit**

```bash
git add .claude/agents/git-push-controller.md
git commit -m "$(cat <<'EOF'
수정: git-push-controller가 master 대신 claude 브랜치로 자동 전환하도록 변경

- "브랜치 권한 확인"(경고 후 사용자 확인 요구) 방식을 "자동 전환"으로 교체 — master/main일 때만 claude 브랜치를 만들거나 이어서 커밋
- 출력 템플릿에 브랜치 전환 여부를 명시하는 필드 추가
- 전환 실패 시 처리 방법을 에러 핸들링 표에 추가

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: `.claude/skills/commitandpush/SKILL.md` Phase 3 프롬프트 갱신

**Files:**
- Modify: `E:\project\IDLE_RPG\.claude\skills\commitandpush\SKILL.md:147`

**Interfaces:**
- Consumes: Task 3에서 갱신된 `git-push-controller.md`의 새 브랜치 전환 로직(같은 개념을 이 프롬프트 한 줄에도 반영해 일관성 유지)
- Produces: `/commitandpush` 스킬이 `git-push-controller` 에이전트를 스폰할 때 전달하는 지시문

- [ ] **Step 1: 147번째 줄을 교체**

기존:
```
    3. 현재 브랜치 확인 및 보호 브랜치 여부 체크
```

교체 후:
```
    3. 현재 브랜치 확인, master/main이면 claude 브랜치로 자동 전환(git-push-controller.md 로직 참조) 후 진행
```

- [ ] **Step 2: 편집 확인**

Run: `Grep -n "claude 브랜치로 자동 전환" .claude/skills/commitandpush/SKILL.md`
Expected: 147번째 줄 근방에서 1건 매칭

- [ ] **Step 3: Commit**

```bash
git add .claude/skills/commitandpush/SKILL.md
git commit -m "$(cat <<'EOF'
수정: commitandpush 스킬의 Phase 3 지시문에 claude 브랜치 전환 반영

git-push-controller 스폰 프롬프트의 "보호 브랜치 체크" 문구를 새 자동 전환 동작(master/main → claude)과 일치하도록 갱신

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: `CLAUDE.md` Git 하네스 변경 이력 기록

**Files:**
- Modify: `E:\project\IDLE_RPG\CLAUDE.md` (Git 자동 커밋 & 푸시 섹션의 변경 이력 표)

**Interfaces:**
- Consumes: 없음
- Produces: 없음 (문서 전용)

- [ ] **Step 1: "## 하네스: Git 자동 커밋 & 푸시 (Git Automator)" 섹션의 변경 이력 표 마지막 행 뒤에 새 행 추가**

```markdown
| 2026-07-04 | Claude 커밋을 master 대신 claude 브랜치로 자동 라우팅 | scripts/auto-commit.ps1, agents/git-push-controller.md, skills/commitandpush/SKILL.md | master를 Claude 자동 커밋으로부터 보호, 사용자가 명시적으로 병합하기 전까지 깨끗하게 유지 |
```

- [ ] **Step 2: 편집 확인**

Run: `Grep -n "claude 브랜치로 자동 라우팅" CLAUDE.md`
Expected: 1건 매칭

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "$(cat <<'EOF'
문서: Git 하네스 변경 이력에 claude 브랜치 자동 라우팅 반영 기록

Co-Authored-By: Claude Sonnet 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: `/commitandpush` 전체 파이프라인 라이브 검증 (master 브랜치 격리 저장소)

**Files:**
- 없음 (기존 `tests/fixtures/commitandpush/setup-clean-repo.ps1` 재사용, 코드 변경 없음)

**Interfaces:**
- Consumes: `E:\project\IDLE_RPG\tests\fixtures\commitandpush\setup-clean-repo.ps1` (기존 파일, master 브랜치·벤치성 변경이 스테이지된 임시 저장소를 만들어 절대 경로를 표준출력 마지막 줄로 반환)
- Consumes: Task 3에서 갱신된 `git-push-controller.md`, Task 4에서 갱신된 `commitandpush/SKILL.md`
- Produces: 없음 (검증 전용)

- [ ] **Step 1: 격리된 clean 저장소 준비 (master 브랜치, claude 브랜치 없음)**

```powershell
$cleanRepo = & "E:\project\IDLE_RPG\tests\fixtures\commitandpush\setup-clean-repo.ps1"
Write-Host "CLEAN_REPO=$cleanRepo"
Push-Location $cleanRepo
git branch --show-current
Pop-Location
```

Run: 위 블록 실행
Expected: `git branch --show-current` 출력이 `master` (claude 브랜치는 아직 없음)

- [ ] **Step 2: git-security-auditor 실행 (PASS 확인)**

`git-security-auditor` 에이전트를 `model: opus`로 스폰해 `$cleanRepo` 디렉토리에서 감사를 수행하고 `$cleanRepo\_workspace\01_security_result.md`에 저장하도록 지시한다(이 세션의 2026-07-03 commitandpush 라이브 스모크와 동일한 프롬프트 패턴 재사용).

Expected 응답: 판정 **PASS**

- [ ] **Step 3: git-commit-writer 실행 (커밋 메시지 생성)**

`git-commit-writer` 에이전트를 `model: opus`로 스폰해 `$cleanRepo` 디렉토리의 스테이지된 변경에 대한 커밋 메시지를 작성하고 `$cleanRepo\_workspace\02_commit_message.txt`에 저장하도록 지시한다.

Expected 응답: `{접두사}: {제목}` 형식의 커밋 메시지 텍스트

- [ ] **Step 4: git-push-controller 실행 (claude 브랜치 자동 전환 + 커밋 검증 — 이번 Task의 핵심)**

`git-push-controller` 에이전트를 `model: opus`로 스폰해 `$cleanRepo` 디렉토리에서 Task 3으로 갱신된 새 지시문(섹션 3: 브랜치 확인 및 claude 브랜치 전환)에 따라 커밋·푸시를 수행하고 `$cleanRepo\_workspace\03_push_result.md`에 저장하도록 지시한다. 원격이 없으므로 push는 생략(로컬 커밋만).

Expected 응답: 커밋 해시 + "브랜치: claude" 언급 (master가 아님)

- [ ] **Step 5: 실제 git 상태로 최종 확인 (에이전트 응답이 아닌 객관적 증거)**

```powershell
Push-Location $cleanRepo
Write-Host "current branch: $(git branch --show-current)"
Write-Host "master log:"
git log master --oneline
Write-Host "claude log:"
git log claude --oneline
Pop-Location
```

Run: 위 블록 실행
Expected:
- `current branch: claude`
- `master log:`에 `init: 픽스처 초기 커밋` 1개만 (README 변경 커밋 없음 — master가 오염되지 않았음)
- `claude log:`에 2개 (`init: 픽스처 초기 커밋` + 방금 생성된 README 변경 커밋)

- [ ] **Step 6: 정리**

```powershell
Remove-Item -Recurse -Force $cleanRepo
```

Run: 위 명령 실행
Expected: 오류 없이 종료

- [ ] **Step 7: 결과 기록 (커밋 불필요 — 검증 전용 Task이므로 코드 변경 없음)**

이 Task는 산출물이 없으므로 git 커밋을 생성하지 않는다. Step 5의 출력을 그대로 사용자에게 보고해 Task 1~5의 변경이 실제 3-에이전트 파이프라인에서 올바르게 동작함을 확인시킨다.

---

## Self-Review 체크리스트 (계획 작성자 수행 완료)

1. **스펙 커버리지**: 설계 문서의 4개 컴포넌트 변경 지점(auto-commit.ps1, git-push-controller.md, commitandpush/SKILL.md, CLAUDE.md) 전부 Task 1·3·4·5에 매핑됨. 설계 문서의 "빌드 검증" 섹션 시나리오 A~D 중 A(Task 1)·B(Task 2)·C(Task 2) 커버, D(원격 전용 claude)는 로컬 bare 저장소 셋업이 과도하게 복잡해 이번 계획에서 의도적으로 생략 — `ls-remote` 코드 경로 자체는 Task 1 Step 2 코드에 존재하며, 실제 IDLE_RPG 원격(origin)에 이후 claude 브랜치가 생기면 자연히 검증됨.
2. **플레이스홀더 스캔**: "TBD", "나중에 구현" 등 없음. 모든 스텝에 실행 가능한 전체 코드/명령이 포함됨.
3. **타입/이름 일관성**: 모든 Task에서 브랜치 이름은 정확히 `claude`(소문자, 하이픈 없음)로 통일. 변수명 `$branch`는 Task 1 안에서만 스코프.
