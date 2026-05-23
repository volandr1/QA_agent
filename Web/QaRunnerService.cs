using QA_agent.QaAgent.Core.Agents;
using QA_agent.QaAgent.Core.Services;
using QA_agent.QaAgent.Core.Tools;
using QaAgent.Core.Models;

namespace QA_agent.Web;

public record LogEntry(string Time, string Message, string CssClass = "");
public record RunRequest(
    string                  SwaggerUrl,
    string                  BaseUrl,
    string                  Model,
    AuthConfig?             Auth         = null,
    IReadOnlyList<string>?  SelectedTags = null);

public class QaRunnerService
{
    private readonly QaAgentConfig _config;
    private readonly ReportService _reportService;

    private CancellationTokenSource? _cts;
    private readonly object _lock = new();

    public bool IsRunning { get; private set; }
    public List<LogEntry> Logs { get; } = new();
    public RunRecord? LastRun { get; private set; }
    public string? ErrorMessage { get; private set; }
    public int Progress { get; private set; }

    public event Action? OnChange;

    public QaRunnerService(QaAgentConfig config, ReportService reportService)
    {
        _config = config;
        _reportService = reportService;
    }

    public bool TryStart(RunRequest request)
    {
        lock (_lock)
        {
            if (IsRunning) return false;
            IsRunning = true;
            ErrorMessage = null;
            Progress = 0;
            Logs.Clear();
        }

        _cts = new CancellationTokenSource();
        _ = RunBackgroundAsync(request, _cts.Token);
        return true;
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    private async Task RunBackgroundAsync(RunRequest request, CancellationToken ct)
    {
        try
        {
            // Step 1: Parse schema
            AddLog("Parsing schema...", "log-info");
            NotifyChange();

            var parser = new SwaggerParser();
            var parseResult = request.SwaggerUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? await parser.ParseUrlAsync(request.SwaggerUrl, ct)
                : await parser.ParseFileAsync(request.SwaggerUrl, ct);

            // Log schema warnings/errors but don't stop if endpoints were found
            foreach (var err in parseResult.Errors)
                AddLog($"Schema warning: {err}", "log-warn");

            if (!parseResult.IsSuccess)
            {
                AddLog("Failed to parse schema — no endpoints found.", "log-error");
                ErrorMessage = parseResult.Errors.Count > 0
                    ? $"Failed to parse Swagger schema: {parseResult.Errors[0]}"
                    : "Failed to parse Swagger schema: no endpoints found.";
                return;
            }

            Progress = 15;

            // Filter by selected tags (if any chosen)
            var filtered = (request.SelectedTags is { Count: > 0 })
                ? parseResult.Endpoints
                    .Where(e => e.Tags.Any(t => request.SelectedTags.Contains(t)))
                    .ToList()
                : parseResult.Endpoints.ToList();

            var endpointsToTest = _config.Execution.MaxEndpoints > 0
                ? filtered.Take(_config.Execution.MaxEndpoints).ToList()
                : filtered;

            if (request.SelectedTags is { Count: > 0 })
                AddLog($"Tag filter: [{string.Join(", ", request.SelectedTags)}] → {endpointsToTest.Count} endpoint(s).", "log-info");

            AddLog($"Found {parseResult.Endpoints.Count} endpoint(s), testing {endpointsToTest.Count}.", "log-ok");
            NotifyChange();

            ct.ThrowIfCancellationRequested();

            // Step 2: Ping Ollama
            AddLog("Checking Ollama...", "log-info");
            NotifyChange();

            using var ollama = new OllamaClient(request.Model, _config.Ollama.Url);
            var isUp = await ollama.PingAsync();
            if (!isUp)
            {
                AddLog($"Ollama not reachable at {_config.Ollama.Url} with model '{request.Model}'.", "log-error");
                ErrorMessage = "Ollama is not running or the model is not available.";
                return;
            }

            AddLog($"Ollama OK — model: {request.Model}", "log-ok");
            Progress = 30;
            NotifyChange();

            ct.ThrowIfCancellationRequested();

            // Step 3: Generate tests
            AddLog("Generating tests via Ollama...", "log-info");
            NotifyChange();

            // Auth: per-run override takes priority over config default
            var auth = (request.Auth?.IsConfigured == true) ? request.Auth : _config.Auth;

            var generator = new TestGenerator(
                ollama,
                request.BaseUrl,
                temperature: _config.Ollama.Temperature,
                maxTokens:   _config.Ollama.MaxTokens,
                auth:        auth);

            var testCases = await generator.GenerateForAllAsync(endpointsToTest, ct);
            AddLog($"Generated {testCases.Count} test case(s).", "log-ok");
            Progress = 55;
            NotifyChange();

            ct.ThrowIfCancellationRequested();

            // Step 4: Run tests via self-healing agent
            AddLog("Running tests (self-healing)...", "log-info");
            NotifyChange();

            var executor = new CodeExecutor(
                testTimeout: TimeSpan.FromSeconds(_config.Execution.TestTimeoutSeconds),
                maxParallelMethods: _config.Execution.MaxParallelMethods);

            var healer = new SelfHealingAgent(
                ollama,
                executor,
                maxAttempts: _config.Execution.SelfHealingAttempts,
                maxConcurrency: _config.Execution.MaxParallelGroups,
                maxAssertionAttempts: _config.Execution.AssertionHealingAttempts);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = await healer.HealAndRunAsync(testCases, ct);
            sw.Stop();

            Progress = 85;
            AddLog($"Execution done in {sw.Elapsed.TotalSeconds:F1}s.", "log-ok");
            NotifyChange();

            ct.ThrowIfCancellationRequested();

            // Step 5: Generate AI analysis
            AddLog("Generating AI analysis...", "log-info");
            NotifyChange();

            var reporter  = new ReportGenerator(ollama);
            var aiAnalysis = await reporter.GetAiAnalysisAsync(results, parseResult.ApiTitle, ct);

            // Step 6: Save results
            AddLog("Saving results...", "log-info");
            NotifyChange();

            var run = await _reportService.SaveAsync(
                results,
                request.BaseUrl,
                parseResult.ApiTitle,
                $"ollama/{request.Model}",
                sw.Elapsed.TotalSeconds,
                aiAnalysis,
                ct);

            LastRun = run;
            Progress = 100;

            var icon = run.Failed + run.Errors == 0 ? "✅" : "⚠️";
            AddLog($"{icon} {run.Passed} passed, {run.Failed} failed, {run.Errors} errors — {run.PassRate}% pass rate", run.Failed + run.Errors == 0 ? "log-ok" : "log-warn");
        }
        catch (OperationCanceledException)
        {
            AddLog("Run cancelled.", "log-warn");
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            AddLog($"Error: {ex.Message}", "log-error");
        }
        finally
        {
            lock (_lock)
            {
                IsRunning = false;
            }
            NotifyChange();
        }
    }

    private void AddLog(string message, string cssClass = "")
    {
        var entry = new LogEntry(DateTime.Now.ToString("HH:mm:ss"), message, cssClass);
        lock (_lock)
        {
            Logs.Add(entry);
        }
    }

    private void NotifyChange() => OnChange?.Invoke();
}
