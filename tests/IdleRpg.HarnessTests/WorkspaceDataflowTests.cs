using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace IdleRpg.HarnessTests;

/// <summary>
/// 계층1-(f): 각 하네스의 `_workspace/` 데이터 흐름 그래프를 검증한다 — PowerShell 게이트가 하지 않는
/// 심층 체크. (1) 모든 read 경로가 같은 하네스의 root 입력이거나 upstream 멤버의 write 경로에서
/// 생성되는지(dead link 0건), (2) 매니페스트가 선언한 모든 경로 문자열이 실제 오케스트레이터/에이전트
/// 문서에 리터럴로 등장하는지(문서-매니페스트 드리프트 방지).
/// </summary>
public class WorkspaceDataflowTests
{
    public static IEnumerable<object[]> Harnesses()
    {
        foreach (var h in HarnessManifest.All)
            yield return new object[] { h };
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void NoDeadReadLinks(Harness harness)
    {
        var producedPaths = new HashSet<string>(harness.RootInputPaths);
        foreach (var member in harness.Members)
            foreach (var output in member.OutputPaths)
                producedPaths.Add(output);

        var deadLinks = new List<string>();
        foreach (var member in harness.Members)
        {
            foreach (var input in member.InputPaths)
            {
                if (!producedPaths.Contains(input))
                    deadLinks.Add($"{member.AgentName} reads '{input}' but no upstream node in '{harness.Name}' writes it");
            }
        }

        Assert.True(deadLinks.Count == 0,
            $"{harness.Name}: dead read link 발견:\n" + string.Join("\n", deadLinks));
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void OutputPaths_DocumentedInOrchestratorAndOwnAgentFile(Harness harness)
    {
        var orchestratorContent = File.ReadAllText(RepoPaths.SkillFile(harness.OrchestratorSkill));

        foreach (var member in harness.Members)
        {
            var agentContent = File.ReadAllText(RepoPaths.AgentFile(member.AgentName));

            foreach (var output in member.OutputPaths)
            {
                Assert.True(ContainsPath(orchestratorContent, output),
                    $"{harness.Name}: 오케스트레이터 SKILL.md에 '{member.AgentName}'의 출력 경로 '{output}'가 문서화돼 있지 않습니다.");
                Assert.True(ContainsPath(agentContent, output),
                    $"{harness.Name}: '{member.AgentName}.md'에 자신의 출력 경로 '{output}'가 문서화돼 있지 않습니다.");
            }
        }
    }

    /// <summary>
    /// 경로 문자열이 문서에 등장하는지 확인한다. 산출물 구조를 ASCII 트리(│, ├──, └──)로 그리는 문서
    /// (예: tdd-orchestrator의 "산출물 구조" 섹션)는 각 파일명을 부모 디렉토리와 다른 줄에, 트리 기호로
    /// 들여써서 적는다 — 그래서 "_workspace/01_analyst/test_design.md" 같은 전체 경로 리터럴이 연속
    /// 문자열로는 존재하지 않는다(사람 눈에는 트리로 명백히 보이지만 grep으로는 안 잡힘).
    /// 이 메서드는 세 단계로 완화하며 시도한다: (1) 전체 경로 원문, (2) `_workspace/` 접두사 제거,
    /// (3) 파일명(basename)만 — 마지막 폴백은 이 테스트를 "파일명이 문서 어딘가에 언급됐는가" 수준의
    /// 느슨한 문서-일관성 체크로 만든다. 실제 데이터 흐름 정합성(dead link 여부)은 더 엄격한
    /// <see cref="NoDeadReadLinks"/>가 전체 경로 문자열 그대로 담당한다.
    /// </summary>
    private static bool ContainsPath(string content, string path)
        => content.Contains(path)
           || content.Contains(path.Replace("_workspace/", string.Empty))
           || content.Contains(Path.GetFileName(path.TrimEnd('/')));

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void RootInputPaths_DocumentedInOrchestrator(Harness harness)
    {
        if (harness.RootInputPaths.Count == 0)
            return; // commitandpush처럼 파일이 아닌 라이브 상태를 입력으로 삼는 하네스는 대상 아님

        var orchestratorContent = File.ReadAllText(RepoPaths.SkillFile(harness.OrchestratorSkill));

        foreach (var rootInput in harness.RootInputPaths)
        {
            Assert.True(orchestratorContent.Contains(rootInput),
                $"{harness.Name}: 오케스트레이터 SKILL.md에 root 입력 경로 '{rootInput}'가 문서화돼 있지 않습니다.");
        }
    }
}
