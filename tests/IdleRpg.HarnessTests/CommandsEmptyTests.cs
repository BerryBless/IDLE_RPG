using System.IO;
using Xunit;

namespace IdleRpg.HarnessTests;

/// <summary>
/// 계층1-(e): 하네스 규칙("커맨드는 생성하지 않는다")이 지켜지는지 검증한다.
/// `.claude/commands/`는 아예 없거나, 존재하더라도 완전히 비어 있어야 한다.
/// </summary>
public class CommandsEmptyTests
{
    [Fact]
    public void CommandsDirectory_AbsentOrEmpty()
    {
        if (!Directory.Exists(RepoPaths.CommandsDir))
            return; // 부재 = 정상

        var hasFiles = Directory.EnumerateFileSystemEntries(RepoPaths.CommandsDir).GetEnumerator().MoveNext();
        Assert.False(hasFiles,
            $".claude/commands/ 가 존재하며 비어있지 않습니다 — 하네스 규칙(커맨드 생성 금지) 위반: {RepoPaths.CommandsDir}");
    }
}
