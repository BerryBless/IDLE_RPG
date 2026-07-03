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
    if ($remote) {
        git -C $repo push -u origin $branch 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            # 커밋은 이미 로컬에 안전하게 완료된 상태이므로 push 실패는 무중단 원칙에 따라
            # 경고만 남기고 훅을 종료한다(exit 0 유지) — 원격 분기/인증 실패는 수동 확인 필요
            Write-Output "::warning:: $branch 브랜치 push 실패 (원격과 분기됐을 수 있음, 수동 확인 필요)"
        }
    }
}
