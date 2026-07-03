using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace IdleRpg.HarnessTests;

/// <summary>
/// 계층1-(c): 실제 `.claude/agents/`·`.claude/skills/` 파일 중 어디서도 참조되지 않는 고아 파일이 없는지
/// 검증한다. 매니페스트에만 의존하지 않고 모든 .md 파일 내용을 정규식으로 스캔한다 —
/// "누가 실제로 이 에이전트/스킬을 호출하는가"를 문서 프로즈에서 직접 확인하는 방식.
/// </summary>
/// <remarks>
/// 스캔 대상 참조 문법(2026-07-03 6개 오케스트레이터 전수 확인):
/// <list type="bullet">
/// <item><description><c>agent_type: "name"</c> — TeamCreate 멤버 선언 (yaml 스타일 5개 하네스)</description></item>
/// <item><description><c>agent_definition="name"</c> — commitandpush의 Agent() 호출 (python 스타일)</description></item>
/// <item><description><c>`/skill-name`</c> — 백틱/공백 뒤에 오는 슬래시+kebab-case 스킬 참조</description></item>
/// </list>
/// 오케스트레이터 스킬 6개 자신은 사용자가 직접 트리거하는 진입점이므로 "참조됨" 요건에서 제외한다.
/// </remarks>
public class OrphanTests
{
    private static readonly Regex AgentTypeRefRegex =
        new("agent_type:\\s*\"([a-z][a-z0-9-]*)\"", RegexOptions.Compiled);

    private static readonly Regex AgentDefinitionRefRegex =
        new("agent_definition=\"([a-z][a-z0-9-]*)\"", RegexOptions.Compiled);

    private static readonly Regex SkillRefRegex =
        new(@"(?<=[\s`(])/([a-z][a-z-]{2,})\b", RegexOptions.Compiled);

    private static IEnumerable<string> AllDefinitionMdFiles()
    {
        foreach (var f in Directory.GetFiles(RepoPaths.AgentsDir, "*.md"))
            yield return f;
        foreach (var dir in Directory.GetDirectories(RepoPaths.SkillsDir))
        {
            var skillMd = Path.Combine(dir, "SKILL.md");
            if (File.Exists(skillMd))
                yield return skillMd;
        }
    }

    private static (HashSet<string> referencedAgents, HashSet<string> referencedSkills) ScanReferences()
    {
        var referencedAgents = new HashSet<string>(StringComparer.Ordinal);
        var referencedSkills = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in AllDefinitionMdFiles())
        {
            var content = File.ReadAllText(file);

            foreach (Match m in AgentTypeRefRegex.Matches(content))
                referencedAgents.Add(m.Groups[1].Value);
            foreach (Match m in AgentDefinitionRefRegex.Matches(content))
                referencedAgents.Add(m.Groups[1].Value);
            foreach (Match m in SkillRefRegex.Matches(content))
                referencedSkills.Add(m.Groups[1].Value);
        }

        return (referencedAgents, referencedSkills);
    }

    /// <summary>
    /// 아웃바운드 방향 검증: agent_type/agent_definition으로 참조된 이름이 실제 에이전트 파일로
    /// 존재하는지 확인한다(오탈자 등으로 참조가 끊긴 경우 탐지). <see cref="NoOrphanAgents"/>가
    /// 인바운드 방향(모든 실제 파일이 참조되는가)을 보므로, 이 테스트와 합쳐야 양방향이 완성된다 —
    /// 자기증명 중 이 방향을 놓치면 "존재하지 않는 에이전트를 가리키는 오탈자 참조"가 감지되지 않는
    /// 결함이 발견됐다(2026-07-03).
    /// </summary>
    [Fact]
    public void NoDanglingAgentTypeReferences()
    {
        var actualAgents = new HashSet<string>(
            Directory.GetFiles(RepoPaths.AgentsDir, "*.md").Select(Path.GetFileNameWithoutExtension)!,
            StringComparer.Ordinal);

        var dangling = new List<string>();
        foreach (var file in AllDefinitionMdFiles())
        {
            var content = File.ReadAllText(file);
            foreach (Match m in AgentTypeRefRegex.Matches(content))
                if (!actualAgents.Contains(m.Groups[1].Value))
                    dangling.Add($"{Path.GetFileName(file)}: agent_type=\"{m.Groups[1].Value}\" — 존재하지 않는 에이전트");
            foreach (Match m in AgentDefinitionRefRegex.Matches(content))
                if (!actualAgents.Contains(m.Groups[1].Value))
                    dangling.Add($"{Path.GetFileName(file)}: agent_definition=\"{m.Groups[1].Value}\" — 존재하지 않는 에이전트");
        }

        Assert.True(dangling.Count == 0, "끊어진 agent_type/agent_definition 참조:\n" + string.Join("\n", dangling));
    }

    [Fact]
    public void NoOrphanAgents()
    {
        var (referencedAgents, _) = ScanReferences();
        var allAgents = Directory.GetFiles(RepoPaths.AgentsDir, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Cast<string>()
            .ToList();

        var orphans = allAgents.Where(a => !referencedAgents.Contains(a)).ToList();

        Assert.True(orphans.Count == 0,
            $"어느 오케스트레이터/에이전트 문서에서도 agent_type/agent_definition으로 참조되지 않는 고아 에이전트: {string.Join(", ", orphans)}");
    }

    [Fact]
    public void NoOrphanSkills()
    {
        var (_, referencedSkills) = ScanReferences();
        var orchestratorSkillNames = new HashSet<string>(
            HarnessManifest.All.Select(h => h.OrchestratorSkill), StringComparer.Ordinal);

        var allSkills = Directory.GetDirectories(RepoPaths.SkillsDir)
            .Select(Path.GetFileName)
            .Cast<string>()
            .ToList();

        var orphans = allSkills
            .Where(s => !referencedSkills.Contains(s) && !orchestratorSkillNames.Contains(s))
            .ToList();

        Assert.True(orphans.Count == 0,
            $"어느 문서에서도 `/skill-name` 형태로 참조되지 않고, 오케스트레이터 진입점도 아닌 고아 스킬: {string.Join(", ", orphans)}");
    }

    [Fact]
    public void ManifestCoversEveryRealAgentAndSkill()
    {
        // 매니페스트(HarnessManifest)가 실제 .claude 트리와 수 단위로 어긋나면(21 에이전트/24 스킬),
        // 이는 신규 에이전트/스킬이 추가됐지만 매니페스트가 갱신되지 않았다는 신호다.
        var actualAgentCount = Directory.GetFiles(RepoPaths.AgentsDir, "*.md").Length;
        var actualSkillCount = Directory.GetDirectories(RepoPaths.SkillsDir).Length;

        var manifestAgentCount = HarnessManifest.All.SelectMany(h => h.Members).Select(m => m.AgentName).Distinct().Count();
        var manifestSkillCount = HarnessManifest.All.Select(h => h.OrchestratorSkill)
            .Concat(HarnessManifest.All.SelectMany(h => h.Members).Select(m => m.SkillRef).Where(s => s is not null)!)
            .Concat(HarnessManifest.All.SelectMany(h => h.ExtraSkillRefs))
            .Distinct()
            .Count();

        Assert.True(manifestAgentCount == actualAgentCount,
            $"매니페스트가 선언한 에이전트 수({manifestAgentCount})가 실제 .claude/agents 파일 수({actualAgentCount})와 다릅니다. HarnessManifest.cs를 갱신하세요.");
        Assert.True(manifestSkillCount == actualSkillCount,
            $"매니페스트가 선언한 스킬 수({manifestSkillCount})가 실제 .claude/skills 디렉토리 수({actualSkillCount})와 다릅니다. HarnessManifest.cs를 갱신하세요.");
    }
}
