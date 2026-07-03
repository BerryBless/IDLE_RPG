using System;
using System.IO;

namespace IdleRpg.HarnessTests;

/// <summary>
/// 테스트 어셈블리의 빌드 출력 위치로부터 저장소 루트 및 `.claude` 하위 경로를 역산하는 헬퍼.
/// </summary>
/// <remarks>
/// <b>[동작 근거]</b> <c>AppContext.BaseDirectory</c>는 테스트 어셈블리 실행 위치
/// (예: <c>tests/IdleRpg.HarnessTests/bin/Debug/net10.0/</c>)이며 빌드 구성(Debug/Release)이나
/// SDK/TFM 변경에 따라 상대 깊이가 달라질 수 있다. 따라서 <c>../../../..</c> 같은 고정 상대경로를
/// 하드코딩하지 않고, <c>IDLE_RPG.sln</c> 파일을 만날 때까지 부모 디렉토리를 순회하여 저장소 루트를
/// 찾는다 — 빌드 출력 경로가 바뀌어도 깨지지 않는다.
/// </remarks>
public static class RepoPaths
{
    private static readonly Lazy<string> RootLazy = new(FindRoot);

    public static string Root => RootLazy.Value;
    public static string ClaudeDir => Path.Combine(Root, ".claude");
    public static string AgentsDir => Path.Combine(ClaudeDir, "agents");
    public static string SkillsDir => Path.Combine(ClaudeDir, "skills");
    public static string CommandsDir => Path.Combine(ClaudeDir, "commands");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "IDLE_RPG.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            $"IDLE_RPG.sln을 '{AppContext.BaseDirectory}'에서부터 부모 디렉토리로 순회했지만 찾지 못했습니다.");
    }

    public static string AgentFile(string agentName) => Path.Combine(AgentsDir, agentName + ".md");

    public static string SkillFile(string skillName) => Path.Combine(SkillsDir, skillName, "SKILL.md");
}
