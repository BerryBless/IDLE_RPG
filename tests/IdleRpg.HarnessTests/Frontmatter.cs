using System.IO;
using System.Text.RegularExpressions;

namespace IdleRpg.HarnessTests;

/// <summary>
/// 에이전트/스킬 .md 파일 최상단 YAML frontmatter(<c>---</c> ... <c>---</c>)에서 추출한 값.
/// </summary>
/// <param name="Present">frontmatter 블록(<c>---</c>로 시작·종료)이 파일 최상단에 존재하는지.</param>
/// <param name="Name">frontmatter의 <c>name:</c> 값. 없으면 null.</param>
/// <param name="Description">frontmatter의 <c>description:</c> 값(따옴표/폴딩 스칼라 언랩 후). 없으면 null.</param>
public sealed record Frontmatter(bool Present, string? Name, string? Description);

/// <summary>
/// name/description 두 키만 다루는 최소 YAML frontmatter 파서.
/// </summary>
/// <remarks>
/// 범용 YAML 파서(YamlDotNet 등) 의존성을 새로 추가하지 않는 이유: 이 저장소의 frontmatter는
/// <c>name</c>과 <c>description</c> 두 키만 사용하고, description은 (1) 큰따옴표로 감싼 한 줄,
/// 또는 (2) <c>&gt;</c> 폴디드 스칼라(들여쓰기된 여러 줄) 두 형태만 쓰인다. 두 형태만 처리하면
/// 충분하므로 정규식 기반 최소 파서로 의존성 없이 해결한다 — 테스트 프로젝트가 순수 관찰자로
/// 남아 검증 로직 자체의 신뢰도를 높인다.
/// </remarks>
public static class FrontmatterParser
{
    private static readonly Regex BlockRegex =
        new(@"\A---\r?\n(?<body>.*?)\r?\n---", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex NameRegex =
        new(@"^name:\s*(?<v>.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex DescQuotedRegex =
        new("^description:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"\\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex DescFoldedRegex =
        new(@"^description:\s*>\s*\r?\n(?<v>(?:[ \t]+\S.*\r?\n?)+)", RegexOptions.Multiline | RegexOptions.Compiled);

    public static Frontmatter Parse(string content)
    {
        var blockMatch = BlockRegex.Match(content);
        if (!blockMatch.Success)
            return new Frontmatter(false, null, null);

        var body = blockMatch.Groups["body"].Value;

        string? name = null;
        var nameMatch = NameRegex.Match(body);
        if (nameMatch.Success)
            name = nameMatch.Groups["v"].Value.Trim();

        string? description = null;
        var descQuoted = DescQuotedRegex.Match(body);
        if (descQuoted.Success)
        {
            description = descQuoted.Groups["v"].Value.Trim();
        }
        else
        {
            var descFolded = DescFoldedRegex.Match(body);
            if (descFolded.Success)
                description = descFolded.Groups["v"].Value.Trim();
        }

        return new Frontmatter(true, name, description);
    }

    public static Frontmatter ParseFile(string path) => Parse(File.ReadAllText(path));
}
