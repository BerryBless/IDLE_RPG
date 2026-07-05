# auto-commit.ps1
# Stop 훅에서 호출 — 메인 세션이 .git/auto_commit_msg.txt 에 남긴 WHY 커밋 메시지를
# 읽어서 커밋 & 푸시한다. 파일이 없거나 형식이 맞지 않으면 접두사 기반 폴백 메시지 사용.
# (이전 방식: Stop 훅이 claude -p 를 직접 호출 → 콜드스타트/stdin 취약성으로 폴백 빈발)

try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# $PSScriptRoot: 스크립트 자신의 디렉토리(<repo>/scripts/)를 런타임에 알려주므로
# 리포 루트를 하드코딩 없이 부모 디렉토리로 유도 — 다른 프로젝트로 이식 시 경로 수정 불필요
$repo = Split-Path $PSScriptRoot -Parent

# 0. commitandpush 파이프라인 실행 중 락 확인
#    [2026-07-05 사고] Stop 훅은 asyncRewake로 백그라운드 실행되고, /commitandpush 파이프라인은
#    서브 에이전트 대기·y/n/edit 확인 등으로 턴을 여러 번 넘긴다. 그 턴 경계마다 Stop 훅이 다시
#    발동할 수 있어, 파이프라인이 보안 감사조차 끝내기 전에 이 훅이 먼저 전체를 커밋해버린
#    사례가 실제로 발생했다(순서 보장 없이 같은 작업 트리를 두 경로가 동시에 커밋 시도).
#    /commitandpush는 시작 시 .git/commitandpush.lock을 만들고 각 단계마다 갱신한다.
#    락이 신선하면(15분 이내) 파이프라인이 살아있는 것으로 보고 자동 커밋을 완전히 건너뛴다.
#    파이프라인이 중간에 죽어 락 해제를 못 했더라도, 15분을 넘기면 안전망 기능이 영구히
#    막히지 않도록 만료된 락으로 간주하고 정상 진행한다.
$lockFile = Join-Path $repo ".git\commitandpush.lock"
if (Test-Path $lockFile) {
    $lockAgeMinutes = ((Get-Date) - (Get-Item $lockFile).LastWriteTime).TotalMinutes
    if ($lockAgeMinutes -lt 15) {
        exit 0
    }
}

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
#    미커밋 변경이 claude 쪽 파일과 충돌해 checkout이 막히는 상황을 피하기 위해, 전환 전 변경을
#    stash로 잠시 치웠다가 전환 직후 되돌린다(stash pop).
#    [2026-07-04 사고] 과거에는 "전환 실패 시 원래 브랜치에 그대로 커밋"(무중단 우선)이었으나,
#    이 폴백이 실제로 master에 커밋 2건을 만들고 origin/master까지 push해버린 사고가 있었다
#    (미커밋 변경이 claude와 충돌 → checkout 실패 → master 폴백 → push).
#    이제는 보호 브랜치 오염 방지가 무중단보다 우선이다: 전환/복원이 끝내 실패하면
#    master/main에는 절대 커밋하지 않고, 변경사항을 보존한 채(stash 또는 작업트리) 중단한다.
$branch = (git -C $repo branch --show-current 2>&1).Trim()
if ($branch -eq 'master' -or $branch -eq 'main') {
    $stashOutput = git -C $repo stash push -u -m 'auto-commit-routing' 2>&1
    $stashCreated = ($LASTEXITCODE -eq 0) -and ($stashOutput -notmatch 'No local changes to save')

    git -C $repo show-ref --verify --quiet refs/heads/claude
    $hasLocalClaude = ($LASTEXITCODE -eq 0)

    if ($hasLocalClaude) {
        git -C $repo checkout claude 2>&1 | Out-Null
    }
    else {
        # 2>$null + $LASTEXITCODE 검사: ls-remote가 네트워크 오류/origin 부재로 실패하면
        # stderr 텍스트가 $remoteClaude에 담겨 truthy로 오판되어 존재하지 않는
        # origin/claude로 checkout을 시도하는 오류를 방지한다 (실패 시 신규 로컬 브랜치로 폴백)
        $remoteClaude = git -C $repo ls-remote --heads origin claude 2>$null
        if ($LASTEXITCODE -eq 0 -and $remoteClaude) {
            git -C $repo checkout -b claude origin/claude 2>&1 | Out-Null
        }
        else {
            git -C $repo checkout -b claude 2>&1 | Out-Null
        }
    }
    $checkoutOk = ($LASTEXITCODE -eq 0)

    # checkout 성공 후에만 claude 쪽에서 stash pop을 시도한다. checkout이 실패했다면
    # 여전히 원래 브랜치에 있으므로 아래 else 분기에서 그 자리에 되돌린다.
    $popOk = $true
    if ($checkoutOk -and $stashCreated) {
        git -C $repo stash pop 2>&1 | Out-Null
        $popOk = ($LASTEXITCODE -eq 0)
    }

    if ($checkoutOk -and $popOk) {
        $branch = 'claude'
    }
    else {
        if (-not $checkoutOk -and $stashCreated) {
            git -C $repo stash pop 2>&1 | Out-Null
        }

        if (-not $checkoutOk) {
            Write-Output "::warning:: claude 브랜치 전환 실패 — master/main 보호를 위해 자동 커밋을 중단합니다. 변경사항은 보존되어 있으니 /commitandpush 로 수동 처리하세요."
        }
        else {
            Write-Output "::warning:: claude 브랜치로 전환은 됐지만 변경사항 복원(stash pop)이 충돌했습니다 — 자동 커밋을 중단합니다. 'git stash list'와 현재 브랜치 상태를 확인해 수동으로 해결하세요."
        }
        exit 0
    }
}

# 3.5. 해결되지 않은 병합/스태시 충돌 경로가 있으면 어떤 브랜치든 절대 커밋하지 않는다.
#      위 stash pop이 충돌하면 브랜치는 이미 claude로 전환된 채 충돌 마커가 파일에 남고
#      $branch가 'claude'로 바뀌어 있어(위 checkoutOk 분기), 이 검사가 없으면 다음 줄의
#      `git add .`가 그 충돌 마커를 그대로 스테이징해 claude에 실수로 커밋 - push해버린다
#      (실측 확인됨: 2026-07-04 하드닝 검증 중 스크래치 클론에서 재현).
$unmergedPaths = @(git -C $repo diff --name-only --diff-filter=U 2>&1 | Where-Object { $_ -ne '' })
if ($unmergedPaths.Count -gt 0) {
    Write-Output "::warning:: 해결되지 않은 병합 충돌 경로가 있어 자동 커밋을 중단합니다: $($unmergedPaths -join ', ') — 수동으로 충돌을 해결한 뒤 /commitandpush 로 처리하세요."
    exit 0
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
    if ($remote) {
        git -C $repo push -u origin $branch 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            # 커밋은 이미 로컬에 안전하게 완료된 상태이므로 push 실패는 무중단 원칙에 따라
            # 경고만 남기고 훅을 종료한다(exit 0 유지) — 원격 분기/인증 실패는 수동 확인 필요
            Write-Output "::warning:: $branch 브랜치 push 실패 (원격과 분기됐을 수 있음, 수동 확인 필요)"
        }
    }
}
