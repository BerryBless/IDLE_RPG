<#
.SYNOPSIS
    계층1 빠른 정적 구조 게이트 — 6개 하네스(에이전트 21·스킬 24) 정의가 규칙을 지키는지 무빌드로 검사한다.

.DESCRIPTION
    dotnet 빌드 없이 초 단위로 실행되는 코스(coarse) 스캔. IdleRpg.HarnessTests(xUnit)가 하는
    매니페스트 기반 심층 검증(_workspace 데이터흐름 그래프 등)의 하위 집합을 정규식으로 빠르게 훑는다.
    Stop 훅이나 CI 프리스텝으로 연결해 매 세션/커밋마다 값싸게 게이트할 목적.

    검사 항목:
      (a) 모든 agents/*.md, skills/*/SKILL.md 가 YAML frontmatter(name+description)로 시작하는가
      (b) 오케스트레이터가 참조하는 agent_type/agent_definition/스킬(/slug)이 실제 파일로 존재하는가
      (c) 참조되지 않는 고아 에이전트/스킬이 없는가
      (d) 팀원 스폰마다 model:"opus" 또는 model="opus" 선언이 있는가
      (e) .claude/commands/ 가 비어있거나 없는가

.EXAMPLE
    pwsh tests/layer1-static-check.ps1

.EXAMPLE
    # 자기증명 테스트용 — 실제 저장소가 아닌 임의의 .claude 트리를 검사
    pwsh tests/layer1-static-check.ps1 -RepoRoot 'C:\temp\broken-claude-fixture'
#>

[CmdletBinding()]
param(
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
$repoRoot = if ($RepoRoot) { $RepoRoot } else { Split-Path -Parent $PSScriptRoot }
$claudeDir = Join-Path $repoRoot '.claude'
$agentsDir = Join-Path $claudeDir 'agents'
$skillsDir = Join-Path $claudeDir 'skills'
$commandsDir = Join-Path $claudeDir 'commands'

$failures = New-Object System.Collections.Generic.List[string]

function Add-Failure([string]$message) {
    $script:failures.Add($message)
}

Write-Host "=== 계층1 정적 구조 게이트 ===" -ForegroundColor Cyan
Write-Host "저장소 루트: $repoRoot"

# ---------------------------------------------------------------------------
# (a) frontmatter 존재 검사
# ---------------------------------------------------------------------------
Write-Host "`n[a] frontmatter 검사..." -ForegroundColor Yellow

$agentFiles = Get-ChildItem -Path $agentsDir -Filter '*.md' -File
$skillFiles = Get-ChildItem -Path $skillsDir -Directory | ForEach-Object {
    $md = Join-Path $_.FullName 'SKILL.md'
    if (Test-Path $md) { Get-Item $md }
}

$allDefinitionFiles = @($agentFiles) + @($skillFiles)

foreach ($file in $allDefinitionFiles) {
    $content = Get-Content -Raw -Path $file.FullName
    if ($content -notmatch '(?s)\A---\r?\n.*?\r?\n---') {
        Add-Failure "frontmatter 없음: $($file.FullName)"
        continue
    }
    $block = [regex]::Match($content, '(?s)\A---\r?\n(?<body>.*?)\r?\n---').Groups['body'].Value
    if ($block -notmatch '(?m)^name:\s*\S+') {
        Add-Failure "name 필드 없음: $($file.FullName)"
    }
    if ($block -notmatch '(?m)^description:\s*\S+') {
        Add-Failure "description 필드 없음: $($file.FullName)"
    }
}
Write-Host "  $($allDefinitionFiles.Count)개 정의 파일 검사 완료"

# ---------------------------------------------------------------------------
# (b) 참조 무결성 + (c) 고아 검사 — 정의 파일 전체에서 참조 패턴 스캔
# ---------------------------------------------------------------------------
Write-Host "`n[b/c] 참조 무결성 · 고아 검사..." -ForegroundColor Yellow

$actualAgentNames = $agentFiles | ForEach-Object { [System.IO.Path]::GetFileNameWithoutExtension($_.Name) }
$actualSkillNames = Get-ChildItem -Path $skillsDir -Directory | ForEach-Object { $_.Name }
$actualAgentSet = New-Object System.Collections.Generic.HashSet[string] (,[string[]]$actualAgentNames)
$actualSkillSet = New-Object System.Collections.Generic.HashSet[string] (,[string[]]$actualSkillNames)

$referencedAgents = New-Object System.Collections.Generic.HashSet[string]
$referencedSkills = New-Object System.Collections.Generic.HashSet[string]

foreach ($file in $allDefinitionFiles) {
    $content = Get-Content -Raw -Path $file.FullName

    foreach ($m in [regex]::Matches($content, 'agent_type:\s*"([a-z][a-z0-9-]*)"')) {
        $refName = $m.Groups[1].Value
        [void]$referencedAgents.Add($refName)
        # 아웃바운드 방향: 참조된 이름이 실제 에이전트 파일로 존재하는가 (예: 오탈자로 끊긴 참조 탐지)
        if (-not $actualAgentSet.Contains($refName)) {
            Add-Failure "존재하지 않는 에이전트를 agent_type으로 참조: '$refName' (in $($file.FullName))"
        }
    }
    foreach ($m in [regex]::Matches($content, 'agent_definition="([a-z][a-z0-9-]*)"')) {
        $refName = $m.Groups[1].Value
        [void]$referencedAgents.Add($refName)
        if (-not $actualAgentSet.Contains($refName)) {
            Add-Failure "존재하지 않는 에이전트를 agent_definition으로 참조: '$refName' (in $($file.FullName))"
        }
    }
    foreach ($m in [regex]::Matches($content, '(?<=[\s`(])/([a-z][a-z-]{2,})\b')) {
        [void]$referencedSkills.Add($m.Groups[1].Value)
        # 스킬 슬러그(/slug)는 일반 텍스트에도 우연히 나타날 수 있어(예: 스킬이 아닌 kebab 표현)
        # 존재 검증은 하지 않고 고아(c) 판정에만 사용한다 — false positive를 피하기 위한 의도적 비대칭.
    }
}

# 오케스트레이터 스킬 6개는 사용자가 직접 트리거하는 진입점이므로 "참조됨" 요건 예외
$orchestratorSkillNames = @(
    'code-review-orchestrator', 'concurrency-guard-orchestrator', 'gc-guard-orchestrator',
    'pipeline-architect-orchestrator', 'tdd-orchestrator', 'commitandpush'
)

foreach ($name in $actualAgentNames) {
    if (-not $referencedAgents.Contains($name)) {
        Add-Failure "고아 에이전트(참조 없음): $name"
    }
}
foreach ($name in $actualSkillNames) {
    if (-not $referencedSkills.Contains($name) -and $orchestratorSkillNames -notcontains $name) {
        Add-Failure "고아 스킬(참조 없음): $name"
    }
}
Write-Host "  에이전트 $($actualAgentNames.Count)개, 스킬 $($actualSkillNames.Count)개 대조 완료"

# ---------------------------------------------------------------------------
# (d) model:"opus" 선언 검사 — 오케스트레이터별 팀원 수만큼 선언됐는지
# ---------------------------------------------------------------------------
Write-Host "`n[d] model:opus 선언 검사..." -ForegroundColor Yellow

# 하네스 이름 -> (스킬파일, 예상 팀원 수, 선언 문법) — HarnessManifest.cs와 동일한 사실을 별도로 인코딩
# (PS와 xUnit이 서로 다른 방식으로 같은 사실을 검증해야 회귀를 이중으로 잡아낸다)
$opusExpectations = @(
    @{ Skill = 'code-review-orchestrator'; Count = 4; Style = 'yaml' }
    @{ Skill = 'concurrency-guard-orchestrator'; Count = 4; Style = 'yaml' }
    @{ Skill = 'gc-guard-orchestrator'; Count = 3; Style = 'yaml' }
    @{ Skill = 'pipeline-architect-orchestrator'; Count = 4; Style = 'yaml' }
    @{ Skill = 'tdd-orchestrator'; Count = 3; Style = 'yaml' }
    @{ Skill = 'commitandpush'; Count = 3; Style = 'python' }
)

foreach ($exp in $opusExpectations) {
    $skillPath = Join-Path (Join-Path $skillsDir $exp.Skill) 'SKILL.md'
    if (-not (Test-Path $skillPath)) {
        Add-Failure "오케스트레이터 스킬 파일 없음: $skillPath"
        continue
    }
    $content = Get-Content -Raw -Path $skillPath
    $pattern = if ($exp.Style -eq 'yaml') { 'model:\s*"opus"' } else { 'model="opus"' }
    $count = [regex]::Matches($content, $pattern).Count
    if ($count -lt $exp.Count) {
        Add-Failure "$($exp.Skill): opus 선언 $count 개 (팀원 $($exp.Count)명보다 적음)"
    }
}
Write-Host "  6개 하네스 opus 선언 개수 검사 완료"

# ---------------------------------------------------------------------------
# (e) .claude/commands/ 비어있음 검사
# ---------------------------------------------------------------------------
Write-Host "`n[e] commands 디렉토리 검사..." -ForegroundColor Yellow

if (Test-Path $commandsDir) {
    $entries = Get-ChildItem -Path $commandsDir -Force
    if ($entries.Count -gt 0) {
        Add-Failure ".claude/commands/ 가 비어있지 않습니다 (하네스 규칙 위반): $commandsDir"
    }
}
Write-Host "  통과 (부재 또는 빈 디렉토리)"

# ---------------------------------------------------------------------------
# 결과 출력
# ---------------------------------------------------------------------------
Write-Host "`n=== 결과 ===" -ForegroundColor Cyan
if ($failures.Count -eq 0) {
    Write-Host "PASS — 모든 정적 구조 검사 통과" -ForegroundColor Green
    exit 0
}
else {
    Write-Host "FAIL — $($failures.Count)건 위반 발견:" -ForegroundColor Red
    foreach ($f in $failures) {
        Write-Host "  - $f" -ForegroundColor Red
    }
    exit 1
}
