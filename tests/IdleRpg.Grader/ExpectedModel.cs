using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace IdleRpg.Grader;

/// <summary>
/// 분석형 하네스(code-review/concurrency-guard/gc-guard) 픽스처의 산출물 아티팩트 하나를 어떻게
/// 읽어야 하는지 선언한다. 실제 findings JSON은 에이전트마다 배열 키·카테고리 필드·심각도 필드
/// 이름이 다르므로(예: gc-guard의 <c>hot_path_allocs</c>/<c>pattern</c> vs deadlock의
/// <c>findings</c>/<c>risk_level</c>) 어댑터로 흡수한다.
/// </summary>
public sealed class ArtifactSpec
{
    /// <summary>`_workspace/` 기준 상대경로 (예: "02_allocation_findings.json").</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>findings 배열이 담긴 최상위 키 (예: "hot_path_allocs", "findings").</summary>
    [JsonPropertyName("arrayKey")]
    public string ArrayKey { get; set; } = "findings";

    /// <summary>카테고리 필드명. null이면 이 아티팩트는 자유 텍스트(title/detail) 기반으로만 매칭한다.</summary>
    [JsonPropertyName("categoryField")]
    public string? CategoryField { get; set; }

    /// <summary>심각도 필드명 — "severity" 또는 "risk_level" 등 에이전트마다 다르다.</summary>
    [JsonPropertyName("severityField")]
    public string SeverityField { get; set; } = "severity";
}

/// <summary>교차검증/리뷰 아티팩트(peer_review.json, deadlock_review.json 등) — survived-recall 계산용.</summary>
public sealed class ReviewSpec
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("scoreField")]
    public string? ScoreField { get; set; }
}

/// <summary>심어놓은 결함 하나 — 채점기가 산출 findings에서 찾아야 할 대상.</summary>
public sealed class ExpectedDefect
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Artifacts 딕셔너리의 키 — 이 결함을 어느 아티팩트에서 찾아야 하는지.</summary>
    [JsonPropertyName("artifact")]
    public string Artifact { get; set; } = "";

    /// <summary>매칭 전략: "category" | "cwe" | "keyword".</summary>
    [JsonPropertyName("match")]
    public string Match { get; set; } = "category";

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("cwe")]
    public string? Cwe { get; set; }

    [JsonPropertyName("keywords")]
    public List<string>? Keywords { get; set; }

    /// <summary>병합 소스(cat/find)로 파일명이 소실되므로 정확한 파일 매칭 대신 라인 힌트(±5)를 쓴다.</summary>
    [JsonPropertyName("lineHint")]
    public int? LineHint { get; set; }

    [JsonPropertyName("minSeverity")]
    public string? MinSeverity { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>분석형 하네스 픽스처의 expected.json 최상위 스키마.</summary>
public sealed class AnalysisExpected
{
    [JsonPropertyName("harness")]
    public string Harness { get; set; } = "";

    [JsonPropertyName("class")]
    public string Class { get; set; } = "analysis";

    [JsonPropertyName("artifacts")]
    public Dictionary<string, ArtifactSpec> Artifacts { get; set; } = new();

    [JsonPropertyName("review")]
    public ReviewSpec? Review { get; set; }

    [JsonPropertyName("expected_defects")]
    public List<ExpectedDefect> ExpectedDefects { get; set; } = new();
}

/// <summary>생성형 하네스(pipeline/tdd/commitandpush)의 결정적 통과 신호 선언.</summary>
public sealed class GenerativeSignal
{
    /// <summary>신호 종류: "contains-all" | "contains-none" | "regex".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "contains-all";

    /// <summary>`_workspace/` 기준 상대경로 — 이 파일의 내용에서 신호를 찾는다.</summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("mustContain")]
    public List<string>? MustContain { get; set; }

    [JsonPropertyName("mustNotContain")]
    public List<string>? MustNotContain { get; set; }

    [JsonPropertyName("regex")]
    public string? Regex { get; set; }
}

/// <summary>생성형 하네스 픽스처의 expected.json 최상위 스키마.</summary>
public sealed class GenerativeExpected
{
    [JsonPropertyName("harness")]
    public string Harness { get; set; } = "";

    [JsonPropertyName("class")]
    public string Class { get; set; } = "generative";

    [JsonPropertyName("signals")]
    public List<GenerativeSignal> Signals { get; set; } = new();
}
