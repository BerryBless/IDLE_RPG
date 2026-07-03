using System;
using System.IO;
using System.Text.Json;
using IdleRpg.Grader;

// ---------------------------------------------------------------------------
// IdleRpg.Grader — 계층2(기능 픽스처) 채점기.
//
// LLM 하네스 실행 결과(_workspace 스냅샷)를 픽스처의 expected.json과 대조해 recall/통과 신호를
// 계산한다. 비결정적 LLM 실행에 의존하므로 이 프로젝트는 IsTestProject가 아닌 일반 Exe이며,
// dotnet test(계층1 CI 게이트)의 대상이 아니다 — 산출물이 없으면 실패가 아니라 skip으로 보고한다.
//
// 사용법:
//   dotnet run --project tests/IdleRpg.Grader -- <fixtureDir> <resultsDir>
//   예) dotnet run --project tests/IdleRpg.Grader -- tests/fixtures/gc-guard tests/results/gc-guard
// ---------------------------------------------------------------------------

if (args.Length < 2)
{
    Console.Error.WriteLine("사용법: dotnet run --project tests/IdleRpg.Grader -- <fixtureDir> <resultsDir>");
    return 2;
}

var fixtureDir = args[0];
var resultsDir = args[1];
var strict = args.Length > 2 && args[2] == "--strict";

var expectedPath = Path.Combine(fixtureDir, "expected.json");
if (!File.Exists(expectedPath))
{
    Console.Error.WriteLine($"expected.json 없음: {expectedPath}");
    return 2;
}

if (!Directory.Exists(resultsDir) || Directory.GetFileSystemEntries(resultsDir).Length == 0)
{
    // 계층2는 LLM 하네스 실행이 선행돼야 한다. 산출물이 없으면 "아직 실행 안 됨"이지 실패가 아니다.
    Console.WriteLine($"SKIP — 결과 디렉토리가 비어있거나 없습니다: {resultsDir}");
    Console.WriteLine("하네스를 먼저 실행하고 _workspace를 이 디렉토리로 스냅샷하세요 (RUNBOOK.md 참고).");
    return 0;
}

var expectedRaw = File.ReadAllText(expectedPath);
using var probe = JsonDocument.Parse(expectedRaw);
var cls = probe.RootElement.TryGetProperty("class", out var clsEl) ? clsEl.GetString() : "analysis";

var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
bool overallPass;
object scorecardForJson;

if (cls == "generative")
{
    var expected = JsonSerializer.Deserialize<GenerativeExpected>(expectedRaw, jsonOptions)
                    ?? throw new InvalidOperationException("expected.json 파싱 실패");
    var scorecard = GenerativeGrader.Grade(expected, resultsDir);
    scorecardForJson = scorecard;
    overallPass = scorecard.AllPassed;

    Console.WriteLine($"=== {scorecard.Harness} (생성형) 채점 결과 ===");
    foreach (var s in scorecard.Signals)
        Console.WriteLine($"  [{(s.Passed ? "PASS" : "FAIL")}] {s.Path} — {s.Detail}");
    Console.WriteLine(scorecard.AllPassed ? "전체 판정: PASS" : "전체 판정: FAIL");
}
else
{
    var expected = JsonSerializer.Deserialize<AnalysisExpected>(expectedRaw, jsonOptions)
                    ?? throw new InvalidOperationException("expected.json 파싱 실패");
    var scorecard = AnalysisGrader.Grade(expected, resultsDir);
    scorecardForJson = scorecard;
    overallPass = scorecard.Recall > 0;

    Console.WriteLine($"=== {scorecard.Harness} (분석형) 채점 결과 ===");
    Console.WriteLine($"Recall: {scorecard.Matched}/{scorecard.Total} ({scorecard.Recall:P0})");
    if (scorecard.ReviewScore is not null)
        Console.WriteLine($"교차검증 확정 점수: {scorecard.ReviewScore}");
    foreach (var d in scorecard.Details)
        Console.WriteLine($"  [{(d.Matched ? "FOUND " : "MISSED")}] {d.Id}" + (d.Note is null ? "" : $" — {d.Note}"));
}

var scorecardPath = Path.Combine(resultsDir, "scorecard.json");
File.WriteAllText(scorecardPath, JsonSerializer.Serialize(scorecardForJson, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\n스코어카드 저장: {scorecardPath}");

// --strict 일 때만 실패를 종료 코드로 반영한다 — 기본은 report-only(계층2는 비결정적이라 CI를 막지 않음).
return strict && !overallPass ? 1 : 0;
