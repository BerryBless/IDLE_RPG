using System;
using System.Collections.Generic;
using System.Linq;

namespace IdleRpg.Grader;

/// <summary>채점 결과 — 결함 하나가 산출 findings에서 발견됐는지 여부.</summary>
public sealed record DefectResult(string Id, bool Matched, string? Note);

/// <summary>분석형 하네스 한 픽스처에 대한 최종 스코어카드.</summary>
public sealed record AnalysisScorecard(
    string Harness,
    int Total,
    int Matched,
    double Recall,
    List<DefectResult> Details,
    double? ReviewScore);

/// <summary>
/// 분석형 하네스(code-review/concurrency-guard/gc-guard) 채점기.
/// expected.json에 심어놓은 결함이 실제 산출 findings JSON에 나타나는지 카테고리/CWE/키워드
/// 매칭으로 확인하고 recall(재현율)을 계산한다.
/// </summary>
/// <remarks>
/// 원본 소스가 병합되며 파일명이 소실되므로 파일명 매칭은 절대 쓰지 않는다 — 카테고리(또는
/// CWE/키워드) + 라인 힌트(±5, 있을 때만) 조합으로만 판정한다. 이는 오탐(false-match)을 줄이면서도
/// 병합으로 인한 라인 밀림에 강건하도록 하는 절충이다.
/// </remarks>
public static class AnalysisGrader
{
    private const int LineTolerance = 5;

    public static AnalysisScorecard Grade(AnalysisExpected expected, string resultsDir)
    {
        var findingsByArtifact = new Dictionary<string, List<NormalizedFinding>?>();
        foreach (var (key, spec) in expected.Artifacts)
            findingsByArtifact[key] = Adapters.LoadFindings(resultsDir, spec);

        var details = new List<DefectResult>();
        foreach (var defect in expected.ExpectedDefects)
        {
            if (!findingsByArtifact.TryGetValue(defect.Artifact, out var findings) || findings is null)
            {
                details.Add(new DefectResult(defect.Id, false, $"아티팩트 '{defect.Artifact}' 산출물 없음"));
                continue;
            }

            var matched = findings.Any(f => IsMatch(f, defect));
            details.Add(new DefectResult(defect.Id, matched, matched ? null : (defect.Note ?? "매칭 실패")));
        }

        double? reviewScore = expected.Review is not null ? Adapters.LoadReviewScore(resultsDir, expected.Review) : null;

        var matchedCount = details.Count(d => d.Matched);
        var total = details.Count;
        return new AnalysisScorecard(
            expected.Harness, total, matchedCount,
            total == 0 ? 1.0 : (double)matchedCount / total,
            details, reviewScore);
    }

    private static bool IsMatch(NormalizedFinding f, ExpectedDefect defect)
    {
        if (defect.MinSeverity is not null && Adapters.RankOf(f.Severity) < Adapters.RankOf(defect.MinSeverity))
            return false;

        if (defect.LineHint is not null && f.Line is not null &&
            Math.Abs(f.Line.Value - defect.LineHint.Value) > LineTolerance)
            return false;

        return defect.Match switch
        {
            "category" => f.Category is not null &&
                           string.Equals(f.Category, defect.Category, StringComparison.OrdinalIgnoreCase),
            "cwe" => f.Cwe is not null && defect.Cwe is not null &&
                     f.Cwe.Contains(defect.Cwe, StringComparison.OrdinalIgnoreCase),
            "keyword" => defect.Keywords is not null &&
                         defect.Keywords.Any(k => ContainsKeyword(f, k)),
            _ => false,
        };
    }

    private static bool ContainsKeyword(NormalizedFinding f, string keyword)
    {
        var haystack = $"{f.Title} {f.Detail}";
        return haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}
