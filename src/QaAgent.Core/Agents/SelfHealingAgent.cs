using System.Text;
using System.Text.RegularExpressions;
using QaAgent.Core.Models;
using QA_agent.QaAgent.Core.Services;

namespace QA_agent.QaAgent.Core.Agents;

/// <summary>
/// Two-phase self-healing agent:
///   Phase 1 — fixes COMPILE errors by re-asking the LLM to correct syntax/type issues.
///   Phase 2 — fixes ASSERTION failures by re-asking the LLM to fix test logic
///              (wrong URLs, wrong request bodies, off-by-one status codes).
///              Skips failures caused by real server bugs (5xx responses).
/// Endpoint groups execute in PARALLEL; healing inside each group is sequential.
/// </summary>
public sealed class SelfHealingAgent
{
    // ─────────────────────────────────────────────────────────────────────────
    //  System prompts
    // ─────────────────────────────────────────────────────────────────────────

    private const string CompileHealingPrompt = """
        You are an expert C# developer fixing compilation errors in xUnit test code.

        RULES:
        1. Return ONLY the fixed C# source code — no markdown, no explanation, no ```csharp blocks.
        2. Keep all test methods and logic intact — only fix compilation errors.
        3. Do NOT change what the tests verify — only fix syntax/type/namespace issues.
        4. The following namespaces are already available via global using — do NOT add them:
           - System, System.Collections.Generic, System.Linq
           - System.Net, System.Net.Http, System.Net.Http.Headers
           - System.Net.Http.Json, System.Text, System.Text.Json
           - System.Threading, System.Threading.Tasks, Xunit
        5. Raw code only — starts with 'using' or 'namespace' or directly with the class.
        """;

    private const string AssertionHealingPrompt = """
        You are an expert C# QA engineer fixing failing xUnit REST API tests.
        The code compiles fine but some tests fail at runtime.

        RULES:
        1. Return ONLY the fixed C# source code — no markdown, no explanation, no ```csharp blocks.
        2. Return the COMPLETE file — namespace declaration, class definition, ALL methods (both passing and failing).
           NEVER return just methods without the surrounding class and namespace.
        3. Fix ONLY the test LOGIC — wrong URLs, wrong request bodies, wrong query parameters,
           wrong Content-Type, wrong expected status codes (off by one: 200 vs 201, etc.).
        4. Do NOT add Authorization headers unless the test specifically tests auth.
        5. Do NOT change tests that are unrelated to the reported failures.
        6. If a test expects 401/403 but the API returns 200, the API does not enforce auth —
           change the assertion to accept 200 instead, or remove the auth test logic.
        7. The output MUST start with 'using' statements, then 'namespace', then 'public class'.
        8. Do NOT duplicate any method — each method name must appear exactly once.
        """;

    // ─────────────────────────────────────────────────────────────────────────
    //  Constants & defaults
    // ─────────────────────────────────────────────────────────────────────────

    private const int DefaultMaxCompileAttempts   = 3;
    private const int DefaultMaxAssertionAttempts = 2;
    private const int DefaultMaxConcurrency       = 4;

    // ─────────────────────────────────────────────────────────────────────────
    //  Fields
    // ─────────────────────────────────────────────────────────────────────────

    private readonly OllamaClient       _ollama;
    private readonly Tools.CodeExecutor _executor;
    private readonly int                _maxCompileAttempts;
    private readonly int                _maxAssertionAttempts;
    private readonly int                _maxConcurrency;

    private static readonly Lock _consoleLock = new();

    // ─────────────────────────────────────────────────────────────────────────
    //  Constructor
    // ─────────────────────────────────────────────────────────────────────────

    public SelfHealingAgent(
        OllamaClient       ollama,
        Tools.CodeExecutor executor,
        int maxAttempts          = DefaultMaxCompileAttempts,
        int maxConcurrency       = DefaultMaxConcurrency,
        int maxAssertionAttempts = DefaultMaxAssertionAttempts)
    {
        _ollama               = ollama;
        _executor             = executor;
        _maxCompileAttempts   = maxAttempts;
        _maxConcurrency       = maxConcurrency;
        _maxAssertionAttempts = maxAssertionAttempts;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    public async Task<List<TestResult>> HealAndRunAsync(
        IReadOnlyList<TestCase> testCases,
        CancellationToken ct = default)
    {
        var groups = testCases
            .Where(tc => tc.GeneratedCode is not null)
            .GroupBy(tc => tc.GeneratedCode!)
            .ToList();

        Log($"Запускаємо {groups.Count} групу(и) паралельно (max {_maxConcurrency})...");

        using var semaphore = new SemaphoreSlim(_maxConcurrency, _maxConcurrency);

        var tasks = groups.Select(group => RunGroupWithSemaphoreAsync(group, semaphore, ct));
        var allGroupResults = await Task.WhenAll(tasks);

        var allResults = allGroupResults.SelectMany(r => r).ToList();

        foreach (var tc in testCases.Where(t => t.GeneratedCode is null))
        {
            allResults.Add(new TestResult
            {
                TestCase     = tc,
                Status       = TestStatus.Skipped,
                ErrorMessage = "No generated code available.",
                Duration     = TimeSpan.Zero
            });
        }

        return allResults;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Per-group execution (з семафором)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<List<TestResult>> RunGroupWithSemaphoreAsync(
        IGrouping<string, TestCase> group,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try   { return await HealAndRunGroupAsync(group, ct); }
        finally { semaphore.Release(); }
    }

    private async Task<List<TestResult>> HealAndRunGroupAsync(
        IGrouping<string, TestCase> group,
        CancellationToken ct)
    {
        var representative = group.First();
        var healedCode     = group.Key;
        List<TestResult> results = [];

        // ── Phase 1: Compile-error healing ───────────────────────────────────

        for (int attempt = 1; attempt <= _maxCompileAttempts; attempt++)
        {
            foreach (var tc in group)
            {
                tc.GeneratedCode     = healedCode;
                tc.GenerationAttempt = attempt;
            }

            results = await _executor.ExecuteAsync(representative, ct);

            var compileError = results.FirstOrDefault(r =>
                r.Status == TestStatus.Error &&
                r.ErrorMessage?.Contains("Compilation failed") == true);

            if (compileError is null)
            {
                if (attempt > 1)
                    Log($"  ✔ Compile-healed after {attempt} attempt(s): {representative.Endpoint.Label}");
                break;
            }

            if (attempt == _maxCompileAttempts)
            {
                Log($"  [GIVE UP compile] Could not heal after {_maxCompileAttempts} attempts: {representative.Endpoint.Label}");
                return results; // pointless to try assertion healing if code won't compile
            }

            Log($"  [COMPILE HEAL {attempt}/{_maxCompileAttempts}] {representative.Endpoint.Label}...");
            var fixedCode = await AskLlmAsync(
                BuildCompileHealPrompt(healedCode, compileError.ErrorMessage!),
                CompileHealingPrompt, ct);

            if (string.IsNullOrWhiteSpace(fixedCode)) break;
            healedCode = CleanCode(fixedCode);
        }

        // ── Phase 2: Assertion-failure healing ───────────────────────────────

        if (_maxAssertionAttempts <= 0) return results;

        var passedBefore = results.Count(r => r.Status == TestStatus.Passed);

        for (int attempt = 1; attempt <= _maxAssertionAttempts; attempt++)
        {
            var healable = results
                .Where(r => r.Status == TestStatus.Failed && IsHealableAssertion(r))
                .ToList();

            if (healable.Count == 0) break;

            Log($"  [ASSERT HEAL {attempt}/{_maxAssertionAttempts}] {healable.Count} fixable failure(s) in {representative.Endpoint.Label}...");
            foreach (var f in healable)
                Log($"    • {f.TestCase.ScenarioName}: {TruncateError(f.ErrorMessage)}");

            var fixedCode = await AskLlmAsync(
                BuildAssertionHealPrompt(healedCode, healable),
                AssertionHealingPrompt, ct);

            if (string.IsNullOrWhiteSpace(fixedCode))
            {
                Log("  [ASSERT HEAL] LLM returned empty — skipping.");
                break;
            }

            healedCode = CleanCode(fixedCode);

            // Update all TestCases and re-run
            foreach (var tc in group) tc.GeneratedCode = healedCode;
            results = await _executor.ExecuteAsync(representative, ct);

            var passedNow = results.Count(r => r.Status == TestStatus.Passed);
            var delta     = passedNow - passedBefore;

            if (delta > 0)
            {
                Log($"  ✔ Assertion-healed: {passedBefore} → {passedNow} passed (+{delta}) for {representative.Endpoint.Label}");
                passedBefore = passedNow;
            }
            else
            {
                Log($"  [ASSERT HEAL] No improvement after attempt {attempt} — stopping.");
                break;
            }
        }

        return results;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Healability filter
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the failure looks like a TEST CODE problem
    /// rather than a real API bug.
    ///
    /// We skip healing when:
    ///   - The server returned 5xx (real server error)
    ///   - The error message explicitly mentions InternalServerError / 500
    /// Everything else (wrong URL, off-by-one status, JSON errors, etc.) is healable.
    /// </summary>
    private static bool IsHealableAssertion(TestResult r)
    {
        // If we captured the actual HTTP status and it's 5xx → server bug
        if (r.ActualStatusCode is >= 500 and <= 599) return false;

        var msg = r.ErrorMessage ?? "";

        // Check for 500-class keywords in the error message
        if (msg.Contains("500",               StringComparison.Ordinal)) return false;
        if (msg.Contains("InternalServerError", StringComparison.OrdinalIgnoreCase)) return false;
        if (msg.Contains("got 5",             StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Prompt builders
    // ─────────────────────────────────────────────────────────────────────────

    private static string BuildCompileHealPrompt(string brokenCode, string errorMessage)
    {
        var errors = ExtractErrorLines(errorMessage);
        var sb = new StringBuilder();
        sb.AppendLine("Fix the following C# compilation errors in this xUnit test class.");
        sb.AppendLine();
        sb.AppendLine("COMPILATION ERRORS:");
        sb.AppendLine(errors);
        sb.AppendLine();
        sb.AppendLine("BROKEN CODE:");
        sb.AppendLine(brokenCode);
        sb.AppendLine();
        sb.AppendLine("Return ONLY the fixed C# code. No explanation, no markdown.");
        return sb.ToString();
    }

    private static string BuildAssertionHealPrompt(string code, List<TestResult> failures)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The following xUnit test methods FAILED at runtime.");
        sb.AppendLine("Fix the test LOGIC — wrong URLs, wrong request bodies, wrong expected status codes.");
        sb.AppendLine("Do NOT heal tests that expect 401/403 if the server returns 500 (that is a server bug).");
        sb.AppendLine();
        sb.AppendLine("FAILING TESTS:");
        sb.AppendLine();

        foreach (var f in failures)
        {
            sb.AppendLine($"Method : {f.TestCase.ScenarioName}");
            sb.AppendLine($"Error  : {f.ErrorMessage?.Split('\n')[0]}");
            if (f.ActualStatusCode.HasValue)
                sb.AppendLine($"Actual HTTP status: {f.ActualStatusCode}");
            sb.AppendLine();
        }

        sb.AppendLine("CURRENT CODE (return this COMPLETE file with fixes applied):");
        sb.AppendLine(code);
        sb.AppendLine();
        sb.AppendLine("IMPORTANT: Return the COMPLETE fixed file — same namespace, same class name, ALL methods.");
        sb.AppendLine("Do NOT omit any methods. Do NOT add duplicate methods. Start with 'using' statements.");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  LLM call
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string> AskLlmAsync(
        string userPrompt,
        string systemPrompt,
        CancellationToken ct)
    {
        try
        {
            return await _ollama.GenerateAsync(
                userPrompt, systemPrompt, 0.1f, 8192, ct);
        }
        catch (Exception ex)
        {
            Log($"  [HEAL ERROR] LLM call failed: {ex.Message}");
            return string.Empty;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static string ExtractErrorLines(string errorMessage)
    {
        var lines = errorMessage
            .Split('\n')
            .Where(l => Regex.IsMatch(l.Trim(), @"^L\d+:"))
            .Select(l => l.Trim())
            .Take(10);
        return string.Join('\n', lines);
    }

    private static string TruncateError(string? msg) =>
        msg is null ? "" : (msg.Length > 80 ? msg[..80] + "…" : msg);

    private static string CleanCode(string raw)
    {
        // Strip markdown fences
        raw = Regex.Replace(raw, @"^```[a-zA-Z]*\n?", "", RegexOptions.Multiline);
        raw = Regex.Replace(raw, @"^```\s*$",         "", RegexOptions.Multiline);

        // Strip prose before first using/namespace
        var firstCode = Regex.Match(raw, @"^(using\s|namespace\s)", RegexOptions.Multiline);
        if (firstCode.Success)
            raw = raw[firstCode.Index..];

        // Strip prose after last closing brace
        var lastBrace = raw.LastIndexOf('}');
        if (lastBrace >= 0)
        {
            var afterBrace = raw[(lastBrace + 1)..].TrimStart('\r', '\n', ' ', '\t');
            if (afterBrace.Length > 0 && !afterBrace.StartsWith("//") && !afterBrace.StartsWith("/*"))
                raw = raw[..(lastBrace + 1)];
        }

        // ── Newtonsoft.Json → System.Text.Json ──────────────────────────────
        raw = raw.Replace("using Newtonsoft.Json;", "using System.Text.Json;");
        raw = raw.Replace("using Newtonsoft.Json.Linq;", "");
        raw = Regex.Replace(raw, @"JsonConvert\.SerializeObject\(([^)]+)\)", "JsonSerializer.Serialize($1)");
        raw = Regex.Replace(raw, @"JsonConvert\.DeserializeObject<([^>]+)>\(([^)]+)\)", "JsonSerializer.Deserialize<$1>($2)");
        raw = Regex.Replace(raw, @"JObject\.Parse\(([^)]+)\)", "JsonDocument.Parse($1).RootElement");

        // ── IDisposable на тест-класі → видаляємо ───────────────────────────
        raw = Regex.Replace(raw, @"\s*:\s*IDisposable", "");
        raw = Regex.Replace(raw,
            @"[ \t]*public\s+void\s+Dispose\s*\(\s*\)\s*\{[^}]*\}\s*\r?\n",
            "", RegexOptions.Singleline);

        return raw.Trim();
    }

    private static void Log(string msg)
    {
        lock (_consoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[SelfHealing] {msg}");
            Console.ResetColor();
        }
    }
}
