<#
.SYNOPSIS
    commitandpush 하네스의 "보안 차단" 음성(negative) 픽스처용 임시 git 저장소를 만든다.

.DESCRIPTION
    격리된 임시 디렉토리에 원격 없는 새 git 저장소를 만들고, AWS 액세스 키 패턴을 포함한 파일을
    스테이지한다. commitandpush를 이 디렉토리에서 실행하면 git-security-auditor가 Phase 1에서
    즉시 FAIL 판정을 내리고 파이프라인이 차단돼야 한다(Phase 2/3 도달 금지, 커밋 생성 금지).
    실제 저장소(E:\project\IDLE_RPG)에는 전혀 영향을 주지 않는다 — 완전히 격리된 cwd, 원격 없음.

.OUTPUTS
    생성된 임시 저장소의 절대 경로(문자열) — 표준출력 마지막 줄.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoPath = Join-Path $env:TEMP ("idlerpg-cap-secret-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $repoPath | Out-Null

Push-Location $repoPath
try {
    git init -q
    git config user.name 'harness-fixture-bot'
    git config user.email 'fixture@local.test'

    # AWS 액세스 키 패턴(AKIA[0-9A-Z]{16}) — git-security-auditor.md의 탐지 패턴 표와 정확히 일치
    @"
# 픽스처: 의도적으로 심어놓은 가짜 시크릿 (실제 자격증명 아님)
AWS_ACCESS_KEY_ID=AKIAABCDEFGHIJKLMNOP
DB_PASSWORD="hunter2hunter2"
"@ | Set-Content -Path (Join-Path $repoPath '.env') -Encoding utf8

    git add .env
}
finally {
    Pop-Location
}

Write-Output $repoPath
