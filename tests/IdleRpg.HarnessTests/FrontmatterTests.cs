using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace IdleRpg.HarnessTests;

/// <summary>
/// 계층1-(a): 모든 에이전트/스킬 정의 파일이 표준 YAML frontmatter(name+description)를 갖추고 있는지 검증한다.
/// 2026-07-03 심층 검증에서 git-* 에이전트 3개가 frontmatter 누락으로 "에이전트 타입 미등록" drift를
/// 일으킨 사례가 있었다 — 이 테스트는 그 회귀를 영구적으로 막는다.
/// </summary>
public class FrontmatterTests
{
    public static IEnumerable<object[]> AgentFiles()
    {
        foreach (var file in Directory.GetFiles(RepoPaths.AgentsDir, "*.md").OrderBy(f => f))
            yield return new object[] { file, Path.GetFileNameWithoutExtension(file) };
    }

    public static IEnumerable<object[]> SkillFiles()
    {
        foreach (var dir in Directory.GetDirectories(RepoPaths.SkillsDir).OrderBy(d => d))
        {
            var skillMd = Path.Combine(dir, "SKILL.md");
            if (File.Exists(skillMd))
                yield return new object[] { skillMd, Path.GetFileName(dir) };
        }
    }

    [Theory]
    [MemberData(nameof(AgentFiles))]
    public void Agent_HasValidFrontmatter(string filePath, string expectedName)
        => AssertFrontmatter(filePath, expectedName);

    [Theory]
    [MemberData(nameof(SkillFiles))]
    public void Skill_HasValidFrontmatter(string filePath, string expectedName)
        => AssertFrontmatter(filePath, expectedName);

    private static void AssertFrontmatter(string filePath, string expectedName)
    {
        var fm = FrontmatterParser.ParseFile(filePath);

        Assert.True(fm.Present, $"{filePath}: frontmatter(---) 블록이 없습니다.");
        Assert.False(string.IsNullOrWhiteSpace(fm.Name), $"{filePath}: name 필드가 없습니다.");
        Assert.Equal(expectedName, fm.Name);
        Assert.False(string.IsNullOrWhiteSpace(fm.Description), $"{filePath}: description 필드가 비어있습니다.");
    }
}
