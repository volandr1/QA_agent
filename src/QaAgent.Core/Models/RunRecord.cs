namespace QaAgent.Core.Models;

/// <summary>
/// Повний запис одного запуску агента — зберігається в .qa-history/.
/// Використовується ReportService для порівняння між запусками.
/// </summary>
public class RunRecord
{
    /// <summary>Унікальний ID запуску.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>Час запуску (UTC).</summary>
    public DateTime RunAt { get; init; } = DateTime.UtcNow;

    /// <summary>Назва API з Swagger-схеми.</summary>
    public string? ApiTitle { get; init; }

    /// <summary>Base URL API що тестувався.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Назва моделі (наприклад, ollama/llama3.1).</summary>
    public string ModelName { get; init; } = "unknown";

    /// <summary>Загальна тривалість запуску.</summary>
    public double TotalDurationSeconds { get; init; }

    // ── Aggregate stats ─────────────────────────────────────────────────────

    public int Total   { get; init; }
    public int Passed  { get; init; }
    public int Failed  { get; init; }
    public int Errors  { get; init; }
    public int Skipped { get; init; }

    public double PassRate => Total > 0 ? Math.Round(Passed * 100.0 / Total, 1) : 0;

    /// <summary>AI-генерований аналіз результатів (plain text від Ollama).</summary>
    public string? AiAnalysis { get; init; }

    /// <summary>Деталі кожного тест-результату.</summary>
    public List<TestResultRecord> Results { get; init; } = [];

    /// <summary>Згенерований C# код по кожному endpoint-у.</summary>
    public List<GeneratedFile> GeneratedFiles { get; init; } = [];
}

/// <summary>
/// Згенерований C# тест-файл для одного endpoint-у.
/// </summary>
public class GeneratedFile
{
    public required string EndpointLabel { get; init; }
    public required string Code          { get; init; }
}

/// <summary>
/// Компактний запис одного результату тесту для збереження в history.
/// Не містить GeneratedCode щоб не роздувати файл.
/// </summary>
public class TestResultRecord
{
    public required string EndpointLabel    { get; init; }
    public required string ScenarioName     { get; init; }
    public required string ScenarioType     { get; init; }
    public required string Status           { get; init; }
    public int             ExpectedStatus   { get; init; }
    public int?            ActualStatus     { get; init; }
    public string?         ErrorMessage     { get; init; }
    public double          DurationMs       { get; init; }

    /// <summary>
    /// Стабільний ключ для порівняння між запусками.
    /// Використовуємо ScenarioType (не ScenarioName) бо LLM може генерувати
    /// трохи різні назви між запусками, а тип — завжди один і той самий.
    /// Наприклад: "PUT /pet::MissingRequired"
    /// </summary>
    public string Key => $"{EndpointLabel}::{ScenarioType}";
}

/// <summary>
/// Результат порівняння двох запусків — показує що змінилось.
/// </summary>
public class RunComparison
{
    /// <summary>Запуск з яким порівнюємо (попередній).</summary>
    public required RunRecord Previous { get; init; }

    /// <summary>Поточний запуск.</summary>
    public required RunRecord Current  { get; init; }

    /// <summary>Тести що стали FAIL (були PASS в попередньому) — регресії.</summary>
    public List<TestResultRecord> NewFailures  { get; init; } = [];

    /// <summary>Тести що стали PASS (були FAIL в попередньому) — виправлення.</summary>
    public List<TestResultRecord> FixedTests   { get; init; } = [];

    /// <summary>Нові тести яких не було в попередньому запуску.</summary>
    public List<TestResultRecord> NewTests     { get; init; } = [];

    /// <summary>Тести що зникли порівняно з попереднім запуском.</summary>
    public List<TestResultRecord> RemovedTests { get; init; } = [];

    /// <summary>Зміна pass rate (поточний - попередній), наприклад +11.1%.</summary>
    public double PassRateDelta => Current.PassRate - Previous.PassRate;

    public bool HasChanges =>
        NewFailures.Count > 0 || FixedTests.Count > 0 ||
        NewTests.Count    > 0 || RemovedTests.Count > 0;
}
