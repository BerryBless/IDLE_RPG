using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace IdleRpg.Grader;

/// <summary>생성형 하네스 신호 하나의 판정 결과.</summary>
public sealed record SignalResult(string Path, bool Passed, string Detail);

/// <summary>생성형 하네스(pipeline/tdd/commitandpush) 한 픽스처에 대한 최종 스코어카드.</summary>
public sealed record GenerativeScorecard(string Harness, bool AllPassed, List<SignalResult> Signals);

/// <summary>
/// 생성형 하네스 채점기. findings JSON이 없으므로 결정적 문자열/정규식 신호로 판정한다 —
/// tdd는 `dotnet test`의 green 마커, pipeline은 감사 리포트의 APPROVE, commitandpush는
/// 보안 게이트의 FAIL/CRITICAL 문자열이다.
/// </summary>
public static class GenerativeGrader
{
    public static GenerativeScorecard Grade(GenerativeExpected expected, string resultsDir)
    {
        var results = new List<SignalResult>();
        foreach (var signal in expected.Signals)
        {
            var fullPath = Path.Combine(resultsDir, signal.Path);
            if (!File.Exists(fullPath))
            {
                results.Add(new SignalResult(signal.Path, false, "산출 파일 없음"));
                continue;
            }

            var content = File.ReadAllText(fullPath);
            results.Add(Evaluate(signal, content));
        }

        return new GenerativeScorecard(expected.Harness, results.All(r => r.Passed), results);
    }

    private static SignalResult Evaluate(GenerativeSignal signal, string content)
    {
        switch (signal.Type)
        {
            case "contains-all":
            {
                var missing = (signal.MustContain ?? new List<string>())
                    .Where(s => !content.Contains(s, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var forbidden = (signal.MustNotContain ?? new List<string>())
                    .Where(s => content.Contains(s, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var passed = missing.Count == 0 && forbidden.Count == 0;
                var detail = passed
                    ? "모든 필수 문자열 발견, 금지 문자열 없음"
                    : $"누락: [{string.Join(", ", missing)}] 금지문자열발견: [{string.Join(", ", forbidden)}]";
                return new SignalResult(signal.Path, passed, detail);
            }
            case "regex":
            {
                if (signal.Regex is null)
                    return new SignalResult(signal.Path, false, "regex 미지정");
                var match = Regex.IsMatch(content, signal.Regex, RegexOptions.Multiline);
                return new SignalResult(signal.Path, match, match ? "패턴 매칭 성공" : $"패턴 불일치: {signal.Regex}");
            }
            default:
                return new SignalResult(signal.Path, false, $"알 수 없는 신호 타입: {signal.Type}");
        }
    }
}
