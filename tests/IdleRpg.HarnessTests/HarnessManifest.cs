using System.Collections.Generic;

namespace IdleRpg.HarnessTests;

/// <summary>
/// 하네스 팀원 한 명(에이전트)의 선언 — 어떤 스킬을 쓰고, `_workspace/` 어디를 읽고 어디에 쓰는지.
/// </summary>
/// <param name="AgentName">`.claude/agents/{AgentName}.md` 파일명(확장자 제외).</param>
/// <param name="SkillRef">전용 스킬 디렉토리명. 전용 스킬이 없으면(예: pipeline-supervisor) null.</param>
/// <param name="InputPaths">이 에이전트가 읽는 `_workspace/` 상대경로 목록(문자열 그대로 두 파일에 등장해야 함).</param>
/// <param name="OutputPaths">이 에이전트가 쓰는 `_workspace/` 상대경로 목록.</param>
public sealed record MemberAgent(
    string AgentName,
    string? SkillRef,
    IReadOnlyList<string> InputPaths,
    IReadOnlyList<string> OutputPaths);

/// <summary>
/// 하네스(오케스트레이터) 하나의 선언적 정의. Layer-1 xUnit 테스트와 Layer-2 채점기가
/// 공유 참조하는 단일 진실 소스 — 2026-07-03 심층 검증에서 수동으로 확인한 사실을 코드로 고정한다.
/// </summary>
/// <param name="Name">사람이 읽는 하네스 이름(로그·리포트용).</param>
/// <param name="OrchestratorSkill">`.claude/skills/{OrchestratorSkill}/SKILL.md`.</param>
/// <param name="ModelDeclStyle">스폰 시 opus 선언 문법. "yaml" → `model: "opus"`, "python" → `model="opus"`.</param>
/// <param name="Members">이 하네스에 소속된 팀원(에이전트) 목록.</param>
/// <param name="ExtraSkillRefs">팀원이 아니지만 오케스트레이터가 직접 호출하는 스킬(예: tdd의 harness-evolve).</param>
/// <param name="RootInputPaths">하네스 최초 입력 — 하네스 내부에서 생성되지 않고 외부(사용자/git)에서 주어짐.</param>
/// <param name="FinalOutputPath">하네스의 최종 산출물 경로(리포트 또는 문서).</param>
public sealed record Harness(
    string Name,
    string OrchestratorSkill,
    string ModelDeclStyle,
    IReadOnlyList<MemberAgent> Members,
    IReadOnlyList<string> ExtraSkillRefs,
    IReadOnlyList<string> RootInputPaths,
    string FinalOutputPath);

/// <summary>
/// 6개 하네스(에이전트 21·스킬 24)의 선언적 매니페스트.
/// 모든 경로 문자열은 실제 SKILL.md/agent.md에서 직접 확인한 리터럴이다 — 이 파일을 벗어난 추측 없음.
/// </summary>
public static class HarnessManifest
{
    public static readonly IReadOnlyList<Harness> All = new[]
    {
        // 1. 종합 코드 리뷰 — 팬아웃/팬인. 입력: diff.txt. 4개 도메인 병렬 → 통합 리포트.
        new Harness(
            Name: "code-review",
            OrchestratorSkill: "code-review-orchestrator",
            ModelDeclStyle: "yaml",
            Members: new[]
            {
                new MemberAgent("architecture-reviewer", "architecture-review",
                    new[] { "_workspace/00_input/diff.txt" },
                    new[] { "_workspace/02_architecture_findings.json" }),
                new MemberAgent("security-reviewer", "security-review",
                    new[] { "_workspace/00_input/diff.txt" },
                    new[] { "_workspace/02_security_findings.json" }),
                new MemberAgent("performance-reviewer", "performance-review",
                    new[] { "_workspace/00_input/diff.txt" },
                    new[] { "_workspace/02_performance_findings.json" }),
                new MemberAgent("style-reviewer", "style-review",
                    new[] { "_workspace/00_input/diff.txt" },
                    new[] { "_workspace/02_style_findings.json" }),
            },
            ExtraSkillRefs: System.Array.Empty<string>(),
            RootInputPaths: new[] { "_workspace/00_input/diff.txt" },
            FinalOutputPath: "_workspace/03_consolidated_report.md"),

        // 2. 동시성 가드 — 하이브리드(팬아웃 + 생성-검증). 입력: source.txt.
        new Harness(
            Name: "concurrency-guard",
            OrchestratorSkill: "concurrency-guard-orchestrator",
            ModelDeclStyle: "yaml",
            Members: new[]
            {
                new MemberAgent("lock-free-enforcer", "lock-free-enforcement",
                    new[] { "_workspace/00_input/source.txt" },
                    new[] { "_workspace/02_lockfree_findings.json" }),
                new MemberAgent("lock-justification-auditor", "lock-justification-audit",
                    new[] { "_workspace/00_input/source.txt" },
                    new[] { "_workspace/02_lockjustification_findings.json" }),
                new MemberAgent("deadlock-analyzer", "deadlock-static-analysis",
                    new[] { "_workspace/00_input/source.txt", "_workspace/02_lockfree_findings.json" },
                    new[] { "_workspace/03_deadlock_analysis.json" }),
                new MemberAgent("deadlock-reviewer", "deadlock-review",
                    new[] { "_workspace/03_deadlock_analysis.json", "_workspace/00_input/source.txt" },
                    new[] { "_workspace/03_deadlock_review.json" }),
            },
            ExtraSkillRefs: System.Array.Empty<string>(),
            RootInputPaths: new[] { "_workspace/00_input/source.txt" },
            FinalOutputPath: "_workspace/04_concurrency_guard_report.md"),

        // 3. GC 가드 — 하이브리드(팬아웃 + 교차검증). 입력: source.txt(*.cs만).
        new Harness(
            Name: "gc-guard",
            OrchestratorSkill: "gc-guard-orchestrator",
            ModelDeclStyle: "yaml",
            Members: new[]
            {
                new MemberAgent("heap-allocation-scanner", "heap-allocation-scan",
                    new[] { "_workspace/00_input/source.txt" },
                    new[] { "_workspace/02_allocation_findings.json" }),
                new MemberAgent("pooling-enforcer", "pooling-enforcement",
                    new[] { "_workspace/00_input/source.txt" },
                    new[] { "_workspace/02_pooling_findings.json" }),
                new MemberAgent("allocation-peer-reviewer", "allocation-peer-review",
                    new[]
                    {
                        "_workspace/02_allocation_findings.json", "_workspace/02_pooling_findings.json",
                        "_workspace/00_input/source.txt",
                    },
                    new[] { "_workspace/03_peer_review.json" }),
            },
            ExtraSkillRefs: System.Array.Empty<string>(),
            RootInputPaths: new[] { "_workspace/00_input/source.txt" },
            FinalOutputPath: "_workspace/04_gc_guard_report.md"),

        // 4. 파이프라인 아키텍처 — 감독자 패턴. 입력: 설계 브리프(스펙, 코드 아님).
        new Harness(
            Name: "pipeline-architect",
            OrchestratorSkill: "pipeline-architect-orchestrator",
            ModelDeclStyle: "yaml",
            Members: new[]
            {
                // pipeline-supervisor: "별도 스킬 없음 — 에이전트 정의의 체크리스트로 직접 수행" (agent .md 확인됨)
                new MemberAgent("pipeline-supervisor", null,
                    new[] { "_workspace/00_design_brief.md" },
                    new[] { "_workspace/02_interface_contract.cs", "_workspace/04_pipeline_architecture.md" }),
                new MemberAgent("io-loop-designer", "io-loop-design",
                    new[] { "_workspace/00_design_brief.md", "_workspace/02_interface_contract.cs" },
                    new[] { "_workspace/02_io_loop/IoLoop.cs" }),
                new MemberAgent("thread-dispatcher-designer", "thread-dispatch-design",
                    new[] { "_workspace/00_design_brief.md", "_workspace/02_interface_contract.cs" },
                    new[] { "_workspace/02_dispatcher/ThreadDispatcher.cs" }),
                new MemberAgent("load-test-auditor", "load-test-audit",
                    new[]
                    {
                        "_workspace/02_io_loop/IoLoop.cs", "_workspace/02_dispatcher/ThreadDispatcher.cs",
                        "_workspace/02_interface_contract.cs",
                    },
                    new[] { "_workspace/03_load_test_audit.md" }),
            },
            ExtraSkillRefs: System.Array.Empty<string>(),
            RootInputPaths: new[] { "_workspace/00_design_brief.md" },
            FinalOutputPath: "_workspace/04_pipeline_architecture.md"),

        // 5. TDD — 파이프라인(Red→Green→Refactor) + 생성-검증(builder↔qa). 입력: 요구사항 스펙.
        new Harness(
            Name: "tdd",
            OrchestratorSkill: "tdd-orchestrator",
            ModelDeclStyle: "yaml",
            Members: new[]
            {
                new MemberAgent("tdd-analyst", "tdd-red-phase",
                    new[] { "_workspace/00_requirements.md" },
                    new[]
                    {
                        "_workspace/01_analyst/Tests/", "_workspace/01_analyst/Src/",
                        "_workspace/01_analyst/test_design.md",
                    }),
                new MemberAgent("tdd-builder", "tdd-green-phase",
                    new[] { "_workspace/01_analyst/Tests/", "_workspace/01_analyst/Src/" },
                    new[] { "_workspace/02_builder/Src/", "_workspace/02_builder/build_notes.md" }),
                new MemberAgent("tdd-qa", "tdd-refactor-phase",
                    new[] { "_workspace/01_analyst/Tests/", "_workspace/02_builder/Src/" },
                    new[]
                    {
                        "_workspace/03_qa/test_results.txt", "_workspace/03_qa/refactor_guide.md",
                        "_workspace/03_qa/Src/",
                    }),
            },
            // harness-evolve는 팀원이 아니라 오케스트레이터가 qa PASS 후 Phase 4에서 직접 호출한다.
            ExtraSkillRefs: new[] { "harness-evolve" },
            RootInputPaths: new[] { "_workspace/00_requirements.md" },
            FinalOutputPath: "_workspace/04_evolution/evolution_report.md"),

        // 6. Git 자동 커밋&푸시 — 순차 서브 에이전트 파이프라인. 입력: 라이브 git 상태(파일 아님).
        //    3개 에이전트 모두 전용 스킬이 없다(commitandpush 스킬 자체가 유일한 스킬).
        //    model 선언도 다른 5개와 달리 python kwarg 문법(model="opus")을 쓴다.
        new Harness(
            Name: "commitandpush",
            OrchestratorSkill: "commitandpush",
            ModelDeclStyle: "python",
            Members: new[]
            {
                new MemberAgent("git-security-auditor", null,
                    System.Array.Empty<string>(),
                    new[] { "_workspace/01_security_result.md" }),
                new MemberAgent("git-commit-writer", null,
                    new[] { "_workspace/01_security_result.md" },
                    new[] { "_workspace/02_commit_message.txt" }),
                new MemberAgent("git-push-controller", null,
                    new[] { "_workspace/02_commit_message.txt" },
                    new[] { "_workspace/03_push_result.md" }),
            },
            ExtraSkillRefs: System.Array.Empty<string>(),
            RootInputPaths: System.Array.Empty<string>(),
            FinalOutputPath: "_workspace/03_push_result.md"),
    };
}
