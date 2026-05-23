using System.Text.Json;
using System.Text.Json.Serialization;
using QaAgent.Core.Models;

namespace QA_agent.QaAgent.Core.Services;

/// <summary>
/// Зберігає history запусків агента у .qa-history/ та порівнює між собою.
/// Кожен запуск — окремий JSON файл.
/// </summary>
public class ReportService
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Config
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented        = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _historyDir;

    public ReportService(string? historyDir = null)
    {
        _historyDir = historyDir
            ?? Path.Combine(Directory.GetCurrentDirectory(), ".qa-history");
        Directory.CreateDirectory(_historyDir);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Save
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Конвертує TestResult-и в RunRecord і зберігає у .qa-history/.
    /// </summary>
    public async Task<RunRecord> SaveAsync(
        IReadOnlyList<TestResult> results,
        string baseUrl,
        string? apiTitle    = null,
        string  modelName   = "unknown",
        double  durationSec = 0,
        string? aiAnalysis  = null,
        CancellationToken ct = default)
    {
        var record = BuildRecord(results, baseUrl, apiTitle, modelName, durationSec, aiAnalysis);
        await SaveRecordAsync(record, ct);
        Log($"Run saved: {record.Id} ({record.Passed}/{record.Total} passed, {record.PassRate}%)");
        return record;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Delete
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Видаляє файл запуску з history. Повертає true якщо файл знайдено і видалено.</summary>
    public bool Delete(string runId)
    {
        var file = Directory
            .GetFiles(_historyDir, $"run-*-{runId}.json")
            .FirstOrDefault();

        if (file is null) return false;

        File.Delete(file);
        Log($"Run deleted: {runId}");
        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Load history
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Повертає всі збережені запуски, відсортовані від нових до старих.
    /// </summary>
    public async Task<List<RunRecord>> GetAllRunsAsync(CancellationToken ct = default)
    {
        var files = Directory
            .GetFiles(_historyDir, "run-*.json")
            .OrderByDescending(f => f)
            .ToList();

        var runs = new List<RunRecord>();
        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var run  = JsonSerializer.Deserialize<RunRecord>(json, JsonOptions);
                if (run is not null) runs.Add(run);
            }
            catch (Exception ex)
            {
                Log($"Warning: could not read {Path.GetFileName(file)}: {ex.Message}");
            }
        }
        return runs;
    }

    /// <summary>
    /// Повертає попередній запуск для того самого BaseUrl.
    /// Використовується для порівняння "що змінилось".
    /// </summary>
    public async Task<RunRecord?> GetPreviousRunAsync(string baseUrl, CancellationToken ct = default)
    {
        var all = await GetAllRunsAsync(ct);
        return all
            .Where(r => r.BaseUrl.TrimEnd('/') == baseUrl.TrimEnd('/'))
            .Skip(1)   // пропускаємо поточний (він вже збережений, тому перший — поточний)
            .FirstOrDefault();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Compare
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Порівнює два запуски і повертає що змінилось:
    /// нові падіння (регресії), виправлені тести, нові/видалені тести.
    /// </summary>
    public static RunComparison Compare(RunRecord previous, RunRecord current)
    {
        // GroupBy замість ToDictionary — обробляємо дублікати ключів.
        // Дублікати виникають коли кілька TestCase-ів одного ендпоінту
        // мають однаковий ScenarioType (наприклад, два MissingRequired).
        // Беремо перший результат по кожному ключу.
        var prevByKey = previous.Results
            .GroupBy(r => r.Key)
            .ToDictionary(g => g.Key, g => g.First());
        var currByKey = current.Results
            .GroupBy(r => r.Key)
            .ToDictionary(g => g.Key, g => g.First());

        var newFailures  = new List<TestResultRecord>();
        var fixedTests   = new List<TestResultRecord>();
        var newTests     = new List<TestResultRecord>();
        var removedTests = new List<TestResultRecord>();

        // Аналізуємо поточні результати
        foreach (var (key, curr) in currByKey)
        {
            if (!prevByKey.TryGetValue(key, out var prev))
            {
                // Новий тест якого не було раніше
                newTests.Add(curr);
                continue;
            }

            var wasPass = prev.Status == "Passed";
            var isPass  = curr.Status == "Passed";

            if (wasPass && !isPass)  newFailures.Add(curr);  // регресія
            if (!wasPass && isPass)  fixedTests.Add(curr);   // виправлено
        }

        // Тести що зникли
        foreach (var (key, prev) in prevByKey)
            if (!currByKey.ContainsKey(key))
                removedTests.Add(prev);

        return new RunComparison
        {
            Previous     = previous,
            Current      = current,
            NewFailures  = newFailures,
            FixedTests   = fixedTests,
            NewTests     = newTests,
            RemovedTests = removedTests
        };
    }

    /// <summary>
    /// Формує Markdown-секцію з порівнянням для вставки у звіт.
    /// </summary>
    public static string BuildComparisonMarkdown(RunComparison cmp)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("## 🔄 Порівняння з попереднім запуском");
        sb.AppendLine();

        var prevTime = cmp.Previous.RunAt.ToString("yyyy-MM-dd HH:mm");
        var delta    = cmp.PassRateDelta;
        var deltaStr = delta >= 0 ? $"+{delta:F1}%" : $"{delta:F1}%";
        var deltaEmoji = delta > 0 ? "📈" : delta < 0 ? "📉" : "➡️";

        sb.AppendLine($"| | Попередній ({prevTime}) | Поточний |");
        sb.AppendLine($"|---|---|---|");
        sb.AppendLine($"| Pass rate | {cmp.Previous.PassRate}% | {cmp.Current.PassRate}% |");
        sb.AppendLine($"| Passed | {cmp.Previous.Passed} | {cmp.Current.Passed} |");
        sb.AppendLine($"| Failed | {cmp.Previous.Failed} | {cmp.Current.Failed} |");
        sb.AppendLine($"| Total | {cmp.Previous.Total} | {cmp.Current.Total} |");
        sb.AppendLine($"| Зміна | | **{deltaEmoji} {deltaStr}** |");
        sb.AppendLine();

        if (!cmp.HasChanges)
        {
            sb.AppendLine("_Змін між запусками не виявлено._");
            sb.AppendLine();
            return sb.ToString();
        }

        // Регресії — найважливіше
        if (cmp.NewFailures.Count > 0)
        {
            sb.AppendLine($"### 🔴 Нові падіння ({cmp.NewFailures.Count}) — РЕГРЕСІЇ");
            sb.AppendLine();
            sb.AppendLine("_Ці тести проходили в попередньому запуску, але зараз падають:_");
            sb.AppendLine();
            foreach (var t in cmp.NewFailures)
                sb.AppendLine($"- ❌ `{t.EndpointLabel}` — {t.ScenarioName}  \n  {t.ErrorMessage?.Split('\n')[0]}");
            sb.AppendLine();
        }

        // Виправлення
        if (cmp.FixedTests.Count > 0)
        {
            sb.AppendLine($"### 🟢 Виправлені тести ({cmp.FixedTests.Count})");
            sb.AppendLine();
            sb.AppendLine("_Ці тести раніше падали, тепер проходять:_");
            sb.AppendLine();
            foreach (var t in cmp.FixedTests)
                sb.AppendLine($"- ✅ `{t.EndpointLabel}` — {t.ScenarioName}");
            sb.AppendLine();
        }

        // Нові тести
        if (cmp.NewTests.Count > 0)
        {
            sb.AppendLine($"### 🆕 Нові тести ({cmp.NewTests.Count})");
            sb.AppendLine();
            foreach (var t in cmp.NewTests)
                sb.AppendLine($"- `{t.EndpointLabel}` — {t.ScenarioName} → {t.Status}");
            sb.AppendLine();
        }

        // Видалені тести
        if (cmp.RemovedTests.Count > 0)
        {
            sb.AppendLine($"### 🗑 Видалені тести ({cmp.RemovedTests.Count})");
            sb.AppendLine();
            foreach (var t in cmp.RemovedTests)
                sb.AppendLine($"- `{t.EndpointLabel}` — {t.ScenarioName}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  History stats
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Повертає тренд pass rate по всіх збережених запусках для одного API.
    /// </summary>
    public async Task<List<(DateTime RunAt, double PassRate, int Total)>> GetTrendAsync(
        string baseUrl,
        int maxRuns = 10,
        CancellationToken ct = default)
    {
        var all = await GetAllRunsAsync(ct);
        return all
            .Where(r => r.BaseUrl.TrimEnd('/') == baseUrl.TrimEnd('/'))
            .Take(maxRuns)
            .OrderBy(r => r.RunAt)
            .Select(r => (r.RunAt, r.PassRate, r.Total))
            .ToList();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Internal helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static RunRecord BuildRecord(
        IReadOnlyList<TestResult> results,
        string baseUrl,
        string? apiTitle,
        string modelName,
        double durationSec,
        string? aiAnalysis = null)
    {
        var records = results.Select(r => new TestResultRecord
        {
            EndpointLabel  = r.TestCase.Endpoint.Label,
            ScenarioName   = r.TestCase.ScenarioName,
            ScenarioType   = r.TestCase.ScenarioType.ToString(),
            Status         = r.Status.ToString(),
            ExpectedStatus = r.TestCase.ExpectedStatusCode,
            ActualStatus   = r.ActualStatusCode,
            ErrorMessage   = r.ErrorMessage?.Length > 200
                             ? r.ErrorMessage[..200] + "..."
                             : r.ErrorMessage,
            DurationMs     = r.Duration.TotalMilliseconds
        }).ToList();

        // Зберігаємо згенерований код — один файл на endpoint (перший непустий)
        var generatedFiles = results
            .Where(r => !string.IsNullOrWhiteSpace(r.TestCase.GeneratedCode))
            .GroupBy(r => r.TestCase.Endpoint.Label)
            .Select(g => new GeneratedFile
            {
                EndpointLabel = g.Key,
                Code          = g.First().TestCase.GeneratedCode!
            })
            .ToList();

        return new RunRecord
        {
            BaseUrl              = baseUrl,
            ApiTitle             = apiTitle,
            ModelName            = modelName,
            TotalDurationSeconds = durationSec,
            AiAnalysis           = aiAnalysis,
            Total                = results.Count,
            Passed               = results.Count(r => r.Status == TestStatus.Passed),
            Failed               = results.Count(r => r.Status == TestStatus.Failed),
            Errors               = results.Count(r => r.Status == TestStatus.Error),
            Skipped              = results.Count(r => r.Status == TestStatus.Skipped),
            Results              = records,
            GeneratedFiles       = generatedFiles
        };
    }

    private async Task SaveRecordAsync(RunRecord record, CancellationToken ct)
    {
        // Назва файлу: run-20260513-130000-{id}.json
        var ts       = record.RunAt.ToString("yyyyMMdd-HHmmss");
        var fileName = $"run-{ts}-{record.Id}.json";
        var path     = Path.Combine(_historyDir, fileName);

        var json = JsonSerializer.Serialize(record, JsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private static void Log(string msg)
    {
        Console.ForegroundColor = ConsoleColor.DarkMagenta;
        Console.WriteLine($"[ReportService] {msg}");
        Console.ResetColor();
    }
}
