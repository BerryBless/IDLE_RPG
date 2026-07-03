using System.Collections.Generic;
using System.IO;
using Xunit;

namespace IdleRpg.HarnessTests;

/// <summary>
/// 계층1-(b): 매니페스트가 선언한 모든 에이전트/스킬 참조가 실제 파일로 존재하는지 검증한다(dead reference 0건).
/// </summary>
public class ReferenceIntegrityTests
{
    public static IEnumerable<object[]> Harnesses()
    {
        foreach (var h in HarnessManifest.All)
            yield return new object[] { h };
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void OrchestratorSkill_Exists(Harness harness)
    {
        var path = RepoPaths.SkillFile(harness.OrchestratorSkill);
        Assert.True(File.Exists(path), $"{harness.Name}: 오케스트레이터 스킬 파일이 없습니다 — {path}");
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void MemberAgentFiles_Exist(Harness harness)
    {
        foreach (var member in harness.Members)
        {
            var path = RepoPaths.AgentFile(member.AgentName);
            Assert.True(File.Exists(path),
                $"{harness.Name}/{member.AgentName}: 에이전트 정의 파일이 없습니다 — {path}");
        }
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void MemberSkillRefs_Exist(Harness harness)
    {
        foreach (var member in harness.Members)
        {
            if (member.SkillRef is null)
                continue; // 전용 스킬 없음으로 선언된 멤버(예: pipeline-supervisor) — 검증 대상 아님

            var path = RepoPaths.SkillFile(member.SkillRef);
            Assert.True(File.Exists(path),
                $"{harness.Name}/{member.AgentName}: 참조 스킬 '{member.SkillRef}' 파일이 없습니다 — {path}");
        }
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void ExtraSkillRefs_Exist(Harness harness)
    {
        foreach (var skillRef in harness.ExtraSkillRefs)
        {
            var path = RepoPaths.SkillFile(skillRef);
            Assert.True(File.Exists(path), $"{harness.Name}: 부가 참조 스킬 '{skillRef}' 파일이 없습니다 — {path}");
        }
    }
}
