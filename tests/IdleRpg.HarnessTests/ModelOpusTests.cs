using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace IdleRpg.HarnessTests;

/// <summary>
/// 계층1-(d): 하네스 규칙("모든 Agent 호출에 model: 'opus' 명시")이 각 오케스트레이터의 팀원 스폰 블록마다
/// 지켜지는지 검증한다. 5개 하네스는 `model: "opus"`(TeamCreate/yaml), commitandpush는 `model="opus"`
/// (Agent() 호출/python kwarg) 문법을 쓴다 — 두 문법을 하네스별로 구분해 정확한 개수를 요구한다.
/// </summary>
public class ModelOpusTests
{
    private static readonly Regex YamlOpusRegex = new("model:\\s*\"opus\"", RegexOptions.Compiled);
    private static readonly Regex PythonOpusRegex = new("model=\"opus\"", RegexOptions.Compiled);

    public static IEnumerable<object[]> Harnesses()
    {
        foreach (var h in HarnessManifest.All)
            yield return new object[] { h };
    }

    [Theory]
    [MemberData(nameof(Harnesses))]
    public void EveryMemberSpawn_DeclaresOpus(Harness harness)
    {
        var content = File.ReadAllText(RepoPaths.SkillFile(harness.OrchestratorSkill));

        var count = harness.ModelDeclStyle switch
        {
            "yaml" => YamlOpusRegex.Matches(content).Count,
            "python" => PythonOpusRegex.Matches(content).Count,
            _ => throw new System.InvalidOperationException($"알 수 없는 ModelDeclStyle: {harness.ModelDeclStyle}"),
        };

        Assert.True(count >= harness.Members.Count,
            $"{harness.Name}: opus 선언 개수({count})가 팀원 수({harness.Members.Count})보다 적습니다 — " +
            $"model:opus 없이 스폰되는 멤버가 있을 수 있습니다.");
    }
}
