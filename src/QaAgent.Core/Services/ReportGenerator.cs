using System.Text;
using QA_agent.QaAgent.Core.Services;
using QaAgent.Core.Models;

namespace QA_agent.QaAgent.Core.Services;

/// <summary>
/// Generates a structured Markdown QA report from test execution results.
/// Optionally uses LLM to write a human-readable bug summary.
/// </summary>
public sealed class ReportGenerator
{
    private readonly OllamaClient? _ollama;

    public ReportGenerator(OllamaClient ollama) => _ollama = ollama;
    public ReportGenerator() { }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a full Markdown report and optionally saves it to a file.
    /// </summary>
    public async Task<string> GenerateAsync(
        ReportInput input,
        string? outputPath    = null,
        CancellationToken ct  = default)
    {
        var sb = new StringBuilder();

        BuildHeader(sb, input);
        BuildSummaryTable(sb, input);

        // Секція порівняння з попереднім запуском (якщо є)
        if (input.Comparison is not null)
            sb.Append(ReportService.BuildComparisonMarkdown(input.Comparison));

        BuildBugSection(sb, input);
        BuildPassedSection(sb, input);
        BuildSkippedSection(sb, input);

        // Optional LLM-generated analysis
        if (_ollama is not null)
        {
            var analysis = await GenerateAnalysisAsync(input, ct);
            if (!string.IsNullOrWhiteSpace(analysis))
            {
                sb.AppendLine();
                sb.AppendLine("## 🤖 AI Analysis");
                sb.AppendLine();
                sb.AppendLine(analysis.Trim());
            }
        }

        BuildFooter(sb, input);

        var report = sb.ToString();

        if (outputPath is not null)
        {
            await File.WriteAllTextAsync(outputPath, report, ct);
            Log($"Report saved to: {outputPath}");
        }

        return report;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Report sections
    // ─────────────────────────────────────────────────────────────────────────

    private static void BuildHeader(StringBuilder sb, ReportInput input)
    {
        sb.AppendLine($"# 🧪 QA Agent Report");
        sb.AppendLine();
        sb.AppendLine($"**API:** {input.ApiTitle ?? "Unknown"}  ");
        sb.AppendLine($"**Base URL:** `{input.BaseUrl}`  ");
        sb.AppendLine($"**Generated:** {input.GeneratedAt:yyyy-MM-dd HH:mm:ss} UTC  ");
        sb.AppendLine($"**Model:** {input.ModelName}  ");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
    }

    private static void BuildSummaryTable(StringBuilder sb, ReportInput input)
    {
        var total   = input.Results.Count;
        var passed  = input.Results.Count(r => r.Status == TestStatus.Passed);
        var failed  = input.Results.Count(r => r.Status == TestStatus.Failed);
        var errors  = input.Results.Count(r => r.Status == TestStatus.Error);
        var skipped = input.Results.Count(r => r.Status == TestStatus.Skipped);

        var passRate = total > 0 ? (passed * 100.0 / total) : 0;
        var health   = passRate switch
        {
            >= 90 => "🟢 Healthy",
            >= 60 => "🟡 Degraded",
            >= 30 => "🟠 Unstable",
            _     => "🔴 Critical"
        };

        sb.AppendLine("## 📊 Summary");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| Total tests | {total} |");
        sb.AppendLine($"| ✅ Passed | {passed} |");
        sb.AppendLine($"| ❌ Failed | {failed} |");
        sb.AppendLine($"| 💥 Errors | {errors} |");
        sb.AppendLine($"| ⏭ Skipped | {skipped} |");
        sb.AppendLine($"| Pass rate | {passRate:F1}% |");
        sb.AppendLine($"| API Health | {health} |");
        sb.AppendLine($"| Endpoints tested | {input.Results.Select(r => r.TestCase.Endpoint.Label).Distinct().Count()} |");
        sb.AppendLine($"| Duration | {input.TotalDuration.TotalSeconds:F1}s |");
        sb.AppendLine();
    }

    private static void BuildBugSection(StringBuilder sb, ReportInput input)
    {
        var bugs = input.Results
            .Where(r => r.Status is TestStatus.Failed or TestStatus.Error)
            .OrderBy(r => r.TestCase.Endpoint.Label)
            .ToList();

        if (bugs.Count == 0)
        {
            sb.AppendLine("## 🐛 Bugs Found");
            sb.AppendLine();
            sb.AppendLine("_No bugs found! All tested endpoints behave as expected._");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"## 🐛 Bugs Found ({bugs.Count})");
        sb.AppendLine();

        // Group by endpoint
        foreach (var group in bugs.GroupBy(r => r.TestCase.Endpoint.Label))
        {
            sb.AppendLine($"### `{group.Key}`");
            sb.AppendLine();

            foreach (var bug in group)
            {
                var severity = ClassifySeverity(bug);
                sb.AppendLine($"#### {severity} {bug.TestCase.ScenarioName}");
                sb.AppendLine();

                if (bug.ActualStatusCode.HasValue)
                {
                    sb.AppendLine($"- **Expected status:** `{bug.TestCase.ExpectedStatusCode}`");
                    sb.AppendLine($"- **Actual status:** `{bug.ActualStatusCode}`");
                }

                if (!string.IsNullOrEmpty(bug.ErrorMessage))
                {
                    var msg = bug.ErrorMessage.Length > 300
                        ? bug.ErrorMessage[..300] + "..."
                        : bug.ErrorMessage;
                    sb.AppendLine($"- **Error:** {msg}");
                }

                sb.AppendLine($"- **Duration:** {bug.Duration.TotalMilliseconds:0}ms");
                sb.AppendLine($"- **Type:** {(bug.Status == TestStatus.Error ? "Compile/Runtime Error" : "Assertion Failure")}");
                sb.AppendLine();
            }
        }
    }

    private static void BuildPassedSection(StringBuilder sb, ReportInput input)
    {
        var passed = input.Results
            .Where(r => r.Status == TestStatus.Passed)
            .ToList();

        if (passed.Count == 0) return;

        sb.AppendLine($"## ✅ Passed Tests ({passed.Count})");
        sb.AppendLine();
        sb.AppendLine("| Endpoint | Scenario | Duration |");
        sb.AppendLine("|----------|----------|----------|");

        foreach (var r in passed)
            sb.AppendLine($"| `{r.TestCase.Endpoint.Label}` | {r.TestCase.ScenarioName} | {r.Duration.TotalMilliseconds:0}ms |");

        sb.AppendLine();
    }

    private static void BuildSkippedSection(StringBuilder sb, ReportInput input)
    {
        var skipped = input.Results.Where(r => r.Status == TestStatus.Skipped).ToList();
        if (skipped.Count == 0) return;

        sb.AppendLine($"## ⏭ Skipped ({skipped.Count})");
        sb.AppendLine();
        foreach (var r in skipped)
            sb.AppendLine($"- `{r.TestCase.Endpoint.Label}` — {r.ErrorMessage}");
        sb.AppendLine();
    }

    private static void BuildFooter(StringBuilder sb, ReportInput input)
    {
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"_Report generated by QA Agent · {input.GeneratedAt:yyyy-MM-dd HH:mm} UTC_");
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  LLM Analysis
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates only the AI analysis text (plain text, no markdown wrapper).
    /// Useful for saving directly to RunRecord.AiAnalysis.
    /// </summary>
    public Task<string> GetAiAnalysisAsync(
        IReadOnlyList<TestResult> results,
        string? apiTitle,
        CancellationToken ct = default)
        => GenerateAnalysisAsync(new ReportInput
        {
            Results   = results,
            BaseUrl   = "",
            ApiTitle  = apiTitle,
            ModelName = "ollama"
        }, ct);

    private async Task<string> GenerateAnalysisAsync(ReportInput input, CancellationToken ct)
    {
        var bugs = input.Results
            .Where(r => r.Status is TestStatus.Failed or TestStatus.Error)
            .ToList();

        if (bugs.Count == 0)
            return "Всі протестовані ендпоінти пройшли перевірку. Проблем не виявлено.";

        var prompt = new StringBuilder();
        prompt.AppendLine("Ти — досвідчений QA-інженер, який аналізує результати автоматизованого тестування API.");
        prompt.AppendLine("Відповідай ВИКЛЮЧНО українською мовою.");
        prompt.AppendLine();
        prompt.AppendLine($"API: {input.ApiTitle}");
        prompt.AppendLine($"Всього тестів: {input.Results.Count}, Провалено: {bugs.Count}");
        prompt.AppendLine();
        prompt.AppendLine("ВАЖЛИВО: Кожна помилка показує який HTTP-статус ОЧІКУВАВ ТЕСТ і що НАСПРАВДІ ПОВЕРНУВ API.");
        prompt.AppendLine("Приклад: Очікувалось=400, Отримано=200 — API прийняв невалідні дані замість відмови.");
        prompt.AppendLine("Приклад: Очікувалось=200, Отримано=500 — API впав з внутрішньою помилкою сервера.");
        prompt.AppendLine();
        prompt.AppendLine("Провалені тести:");

        foreach (var bug in bugs.Take(10))
        {
            var actual = bug.ActualStatusCode?.ToString() ?? "помилка компіляції/виконання";
            var meaning = (bug.TestCase.ExpectedStatusCode, bug.ActualStatusCode) switch
            {
                (400, 200) => "API прийняв невалідні дані замість відмови",
                (404, 200) => "API повернув OK для неіснуючого ресурсу",
                (401, 200) => "API дозволив доступ без автентифікації",
                (401, var s) when s >= 500 => "API впав при запиті без токена",
                (_, var s) when s >= 500   => "API повернув внутрішню помилку сервера",
                _                          => ""
            };
            prompt.AppendLine($"- [{bug.TestCase.ScenarioName}] {bug.TestCase.Endpoint.Label}");
            prompt.AppendLine($"  Очікувалось HTTP {bug.TestCase.ExpectedStatusCode}, отримано HTTP {actual}" +
                              (meaning.Length > 0 ? $" → {meaning}" : ""));
        }

        prompt.AppendLine();
        prompt.AppendLine("Напиши 3-5 речень українською мовою, що аналізують ці помилки:");
        prompt.AppendLine("- Що API робить неправильно?");
        prompt.AppendLine("- Що розробник повинен виправити?");
        prompt.AppendLine("Тільки звичайний текст, без markdown, без списків.");

        try
        {
            Log("Generating AI analysis...");
            return await _ollama!.GenerateAsync(prompt.ToString(), temperature: 0.3f, maxTokens: 512, ct: ct);
        }
        catch (Exception ex)
        {
            Log($"AI analysis failed: {ex.Message}");
            return string.Empty;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string ClassifySeverity(TestResult r)
    {
        if (r.Status == TestStatus.Error) return "🔴 **[CRITICAL]**";

        return r.TestCase.ScenarioType switch
        {
            TestScenarioType.HappyPath    => "🔴 **[HIGH]**",
            TestScenarioType.Unauthorized => "🟠 **[MEDIUM]**",
            TestScenarioType.NotFound     => "🟡 **[LOW]**",
            _                             => "🟡 **[LOW]**"
        };
    }

    private static void Log(string msg)
    {
        Console.ForegroundColor = ConsoleColor.DarkGreen;
        Console.WriteLine($"[ReportGenerator] {msg}");
        Console.ResetColor();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Input model
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ReportInput
{
    public required IReadOnlyList<TestResult> Results       { get; init; }
    public required string                    BaseUrl       { get; init; }
    public string?                            ApiTitle      { get; init; }
    public string                             ModelName     { get; init; } = "unknown";
    public DateTime                           GeneratedAt   { get; init; } = DateTime.UtcNow;
    public TimeSpan                           TotalDuration { get; init; }

    /// <summary>Порівняння з попереднім запуском. Null = перший запуск.</summary>
    public RunComparison?                     Comparison    { get; init; }
}