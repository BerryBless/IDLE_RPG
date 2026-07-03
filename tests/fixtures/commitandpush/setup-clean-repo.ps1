<#
.SYNOPSIS
    commitandpush 하네스의 "정상 커밋" 양성(positive) 픽스처용 임시 git 저장소를 만든다.

.DESCRIPTION
    격리된 임시 디렉토리에 원격 없는 새 git 저장소를 만들고, 민감 정보가 없는 벤치성 변경을
    미리 스테이지한다(Phase 0의 "전체 스테이지?" y/n 프롬프트를 건너뛰기 위함). commitandpush를
    이 디렉토리에서 실행하면 보안 PASS → 커밋 메시지 생성 → (y 확인 후) 로컬 커밋까지 도달해야
    한다. 원격이 없으므로 "로컬 커밋만 완료"로 끝나는 것이 정상 흐름이다.
    실제 저장소(E:\project\IDLE_RPG)에는 전혀 영향을 주지 않는다 — 완전히 격리된 cwd.

.OUTPUTS
    생성된 임시 저장소의 절대 경로(문자열) — 표준출력 마지막 줄.
#>

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$repoPath = Join-Path $env:TEMP ("idlerpg-cap-clean-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $repoPath | Out-Null

Push-Location $repoPath
try {
    git init -q
    git config user.name 'harness-fixture-bot'
    git config user.email 'fixture@local.test'

    # 최초 커밋이 있어야 git log --oneline -10(git-commit-writer의 스타일 학습 단계)이 정상 동작한다.
    'initial' | Set-Content -Path (Join-Path $repoPath 'README.md') -Encoding utf8
    git add README.md
    git commit -q -m 'init: 픽스처 초기 커밋'

    'idle rpg 계정 레벨업 계산 유틸' | Add-Content -Path (Join-Path $repoPath 'README.md') -Encoding utf8
    git add README.md
}
finally {
    Pop-Location
}

Write-Output $repoPath
