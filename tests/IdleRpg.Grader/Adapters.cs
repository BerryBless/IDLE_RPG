using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace IdleRpg.Grader;

/// <summary>
/// 하나의 findings 항목을 하네스별 필드명 차이를 흡수해 공통 형태로 정규화한 결과.
/// </summary>
public sealed record NormalizedFinding(
    string? Category,
    string? Severity,
    string? Cwe,
    string? Title,
    string? Detail,
    int? Line);

/// <summary>
/// 하네스마다 다른 findings JSON 스키마(배열 키, 카테고리 필드명, 심각도 필드명)를
/// <see cref="ArtifactSpec"/> 선언에 따라 읽어 공통 형태로 변환하는 어댑터.
/// </summary>
/// <remarks>
/// 원본 소스가 `cat`/`find -exec cat`로 병합되며 파일명이 소실되므로("source.txt:42" 형태만 남음),
/// <c>file</c> 필드에서 라인 번호만 추출하고 파일명은 매칭에 절대 쓰지 않는다.
/// </remarks>
public static class Adapters
{
    private static readonly Dictionary<string, int> SeverityRank = new(StringComparer.OrdinalIgnoreCase)
    {
        ["critical"] = 4,
        ["high"] = 3,
        ["medium"] = 2,
        ["low"] = 1,
    };

    public static int RankOf(string? severity)
        => severity is not null && SeverityRank.TryGetValue(severity, out var r) ? r : 0;

    /// <summary>지정된 결과 디렉토리에서 아티팩트 JSON 파일을 읽어 findings 목록을 정규화한다.</summary>
    /// <returns>파일이 없으면 null(하네스 실행 산출물 없음 — 채점 skip 신호).</returns>
    public static List<NormalizedFinding>? LoadFindings(string resultsDir, ArtifactSpec spec)
    {
        var fullPath = Path.Combine(resultsDir, spec.Path);
        if (!File.Exists(fullPath))
            return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(fullPath));
        if (!doc.RootElement.TryGetProperty(spec.ArrayKey, out var arrayElement) ||
            arrayElement.ValueKind != JsonValueKind.Array)
            return new List<NormalizedFinding>(); // 배열 키가 없으면 findings 0건으로 취급

        var result = new List<NormalizedFinding>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            string? category = spec.CategoryField is not null && item.TryGetProperty(spec.CategoryField, out var catEl)
                ? catEl.GetString()
                : null;
            string? severity = item.TryGetProperty(spec.SeverityField, out var sevEl) ? sevEl.GetString() : null;
            string? cwe = item.TryGetProperty("cwe", out var cweEl) ? cweEl.GetString() : null;
            string? title = item.TryGetProperty("title", out var titleEl) ? titleEl.GetString() : null;
            string? detail = item.TryGetProperty("detail", out var detailEl) ? detailEl.GetString() : null;
            string? file = item.TryGetProperty("file", out var fileEl) ? fileEl.GetString() : null;

            result.Add(new NormalizedFinding(category, severity, cwe, title, detail, ExtractLine(file)));
        }

        return result;
    }

    /// <summary>리뷰(교차검증) 아티팩트에서 확정 점수(final_score/score)를 읽는다. 없으면 null.</summary>
    public static double? LoadReviewScore(string resultsDir, ReviewSpec spec)
    {
        var fullPath = Path.Combine(resultsDir, spec.Path);
        if (!File.Exists(fullPath) || spec.ScoreField is null)
            return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(fullPath));
        return doc.RootElement.TryGetProperty(spec.ScoreField, out var scoreEl) && scoreEl.TryGetDouble(out var v)
            ? v
            : null;
    }

    /// <summary>"source.txt:42" 형태의 file 필드에서 라인 번호만 추출한다 — 파일명은 신뢰하지 않는다.</summary>
    private static int? ExtractLine(string? file)
    {
        if (string.IsNullOrEmpty(file))
            return null;
        var idx = file.LastIndexOf(':');
        if (idx < 0 || idx == file.Length - 1)
            return null;
        return int.TryParse(file[(idx + 1)..].Trim(), out var n) ? n : null;
    }
}
