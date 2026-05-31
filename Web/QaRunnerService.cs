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
    AuthConfig?             Auth                = null,
    IReadOnlyList<string>?  SelectedTags        = null,
    bool                    SkipPassedEndpoints = false,
    bool                    MonitorAfterRun     = false);

public class QaRunnerService
{
    private readonly QaAgentConfig _config;
    private readonly ReportService _reportService;

    // ── Run state ────────────────────────────────────────────────────────────
    private CancellationTokenSource? _cts;
    private readonly object          _lock = new();

    public bool       IsRunning    { get; private set; }
    public List<LogEntry> Logs     { get; } = new();
    public RunRecord? LastRun      { get; private set; }
    public string?    ErrorMessage { get; private set; }
    public int        Progress     { get; private set; }

    // ── Monitor state ────────────────────────────────────────────────────────
    private CancellationTokenSource? _monitorCts;

    public bool    IsMonitoring    { get; private set; }
    public string? MonitoringUrl   { get; private set; }
    public string  MonitorStatus   { get; private set; } = "";

    public event Action? OnChange;

    public QaRunnerService(QaAgentConfig config, ReportService reportService)
    {
        _config        = config;
        _reportService = reportService;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    public bool TryStart(RunRequest request)
    {
        lock (_lock)
        {
            if (IsRunning) return false;

            // Stop any existing monitor before starting a fresh run
            StopMonitoring();

            IsRunning    = true;
            ErrorMessage = null;
            Progress     = 0;
            Logs.Clear();
        }

        _cts = new CancellationTokenSource();
        _ = RunBackgroundAsync(request, _cts.Token);
        return true;
    }

    public void Cancel()
    {
        _cts?.Cancel();
        StopMonitoring();
    }

    public void StopMonitoring()
    {
        _monitorCts?.Cancel();
        _monitorCts  = null;
        IsMonitoring = false;
        MonitorStatus = "";
        MonitoringUrl = null;
        NotifyChange();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Run pipeline
    // ─────────────────────────────────────────────────────────────────────────

    private async Task RunBackgroundAsync(RunRequest request, CancellationToken ct)
    {
        var runSuccess = false;

        try
        {
            // Step 1: Parse schema
            AddLog("Parsing schema...", "log-info");
            NotifyChange();

            var parser = new SwaggerParser();
            var parseResult = request.SwaggerUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? await parser.ParseUrlAsync(request.SwaggerUrl, ct)
                : await parser.ParseFileAsync(request.SwaggerUrl, ct);

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

            // Filter by selected tags
            var filtered = (request.SelectedTags is { Count: > 0 })
                ? parseResult.Endpoints.Where(e => e.Tags.Any(t => request.SelectedTags.Contains(t))).ToList()
                : parseResult.Endpoints.ToList();

            var endpointsToTest = _config.Execution.MaxEndpoints > 0
                ? filtered.Take(_config.Execution.MaxEndpoints).ToList()
                : filtered.ToList();

            if (request.SelectedTags is { Count: > 0 })
                AddLog($"Tag filter: [{string.Join(", ", request.SelectedTags)}] → {endpointsToTest.Count} endpoint(s).", "log-info");

            // Smart filter: skip already-passed endpoints
            if (request.SkipPassedEndpoints)
            {
                var prevRun = await _reportService.GetPreviousRunAsync(request.BaseUrl);
                if (prevRun is not null)
                {
                    var alreadyPassed = prevRun.Results
                        .Where(r => r.Status == "Passed")
                        .Select(r => r.EndpointLabel)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    var before = endpointsToTest.Count;
                    endpointsToTest = endpointsToTest.Where(e => !alreadyPassed.Contains(e.Label)).ToList();

                    var skipped = before - endpointsToTest.Count;
                    if (skipped > 0)
                        AddLog($"⏭ Skipped {skipped} already-passed endpoint(s). Testing {endpointsToTest.Count} new/failed.", "log-info");

                    if (endpointsToTest.Count == 0)
                    {
                        AddLog("✅ All endpoints already tested and passing. Nothing new to test.", "log-ok");
                        Progress     = 100;
                        runSuccess   = true;
                        NotifyChange();
                        return;
                    }
                }
            }

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

            var auth = (request.Auth?.IsConfigured == true) ? request.Auth : _config.Auth;
            var generator = new TestGenerator(ollama, request.BaseUrl,
                temperature: _config.Ollama.Temperature,
                maxTokens:   _config.Ollama.MaxTokens,
                auth:        auth);

            var testCases = await generator.GenerateForAllAsync(endpointsToTest, ct);
            AddLog($"Generated {testCases.Count} test case(s).", "log-ok");
            Progress = 55;
            NotifyChange();

            ct.ThrowIfCancellationRequested();

            // Step 4: Compile, self-heal, execute
            AddLog("Running tests (self-healing)...", "log-info");
            NotifyChange();

            var executor = new CodeExecutor(
                testTimeout:        TimeSpan.FromSeconds(_config.Execution.TestTimeoutSeconds),
                maxParallelMethods: _config.Execution.MaxParallelMethods);

            var healer = new SelfHealingAgent(ollama, executor,
                maxAttempts:          _config.Execution.SelfHealingAttempts,
                maxConcurrency:       _config.Execution.MaxParallelGroups,
                maxAssertionAttempts: _config.Execution.AssertionHealingAttempts);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = await healer.HealAndRunAsync(testCases, ct);
            sw.Stop();

            Progress = 85;
            AddLog($"Execution done in {sw.Elapsed.TotalSeconds:F1}s.", "log-ok");
            NotifyChange();

            ct.ThrowIfCancellationRequested();

            // Step 5: AI analysis
            AddLog("Generating AI analysis...", "log-info");
            NotifyChange();

            var reporter   = new ReportGenerator(ollama);
            var aiAnalysis = await reporter.GetAiAnalysisAsync(results, parseResult.ApiTitle, ct);

            // Step 6: Save
            AddLog("Saving results...", "log-info");
            NotifyChange();

            var run = await _reportService.SaveAsync(
                results, request.BaseUrl, parseResult.ApiTitle,
                $"ollama/{request.Model}", sw.Elapsed.TotalSeconds, aiAnalysis, ct);

            LastRun    = run;
            Progress   = 100;
            runSuccess = true;

            var icon = run.Failed + run.Errors == 0 ? "✅" : "⚠️";
            AddLog($"{icon} {run.Passed} passed, {run.Failed} failed, {run.Errors} errors — {run.PassRate}% pass rate",
                   run.Failed + run.Errors == 0 ? "log-ok" : "log-warn");

            // Step 7: Telegram
            if (_config.Telegram.IsConfigured)
            {
                AddLog("Sending Telegram notification...", "log-info");
                NotifyChange();
                var tgSvc = new TelegramNotificationService(_config.Telegram);
                var tgErr = await tgSvc.SendAsync(run, ct);
                AddLog(tgErr is null ? "📨 Telegram message sent" : $"📨 Telegram failed: {tgErr}",
                       tgErr is null ? "log-ok" : "log-warn");
            }
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
            lock (_lock) { IsRunning = false; }
            NotifyChange();

            // Start change monitor if requested and run succeeded
            if (runSuccess && request.MonitorAfterRun)
                StartMonitoring(request);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Change monitor
    // ─────────────────────────────────────────────────────────────────────────

    private void StartMonitoring(RunRequest request)
    {
        _monitorCts   = new CancellationTokenSource();
        IsMonitoring  = true;
        MonitoringUrl = request.SwaggerUrl;

        var intervalMin = Math.Max(1, _config.Schedule.CheckIntervalMinutes);
        MonitorStatus = $"Checking every {intervalMin} min";
        NotifyChange();

        _ = MonitorChangesAsync(request, _monitorCts.Token);
    }

    private async Task MonitorChangesAsync(RunRequest request, CancellationToken ct)
    {
        var parser          = new SwaggerParser();
        var intervalMin     = Math.Max(1, _config.Schedule.CheckIntervalMinutes);
        var lastFingerprint = await GetFingerprintAsync(parser, request.SwaggerUrl, ct);

        AddLog($"👁 Monitoring for spec changes every {intervalMin} min...", "log-info");
        NotifyChange();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(intervalMin), ct);

                if (ct.IsCancellationRequested) break;

                MonitorStatus = $"Checking... ({DateTime.Now:HH:mm})";
                NotifyChange();

                var (currentFingerprint, currentResult) = await GetFingerprintWithResultAsync(parser, request.SwaggerUrl, ct);

                if (currentFingerprint == lastFingerprint)
                {
                    MonitorStatus = $"No changes — last check {DateTime.Now:HH:mm}";
                    NotifyChange();
                    continue;
                }

                // Compute what changed
                var oldLabels  = FingerprintToSet(lastFingerprint);
                var newLabels  = FingerprintToSet(currentFingerprint);
                var added      = newLabels.Except(oldLabels).ToList();
                var removed    = oldLabels.Except(newLabels).ToList();

                AddLog($"🔄 Spec changed! +{added.Count} new, -{removed.Count} removed endpoint(s).", "log-warn");
                foreach (var a in added)   AddLog($"   🆕 {a}", "log-info");
                foreach (var r in removed) AddLog($"   🗑 {r}", "log-warn");

                lastFingerprint = currentFingerprint;
                MonitorStatus   = $"Change detected at {DateTime.Now:HH:mm} — retesting";
                NotifyChange();

                // Wait until current run finishes (if any)
                while (IsRunning && !ct.IsCancellationRequested)
                    await Task.Delay(2000, ct);

                if (ct.IsCancellationRequested) break;

                // Retest only new/failed endpoints
                var retestRequest = request with { SkipPassedEndpoints = true, MonitorAfterRun = false };
                var started = TryStart(retestRequest);
                if (started)
                    AddLog("🚀 Auto-run triggered for changed endpoints.", "log-info");

                // Wait for the new run to finish, then resume monitoring
                while (IsRunning && !ct.IsCancellationRequested)
                    await Task.Delay(2000, ct);

                if (!ct.IsCancellationRequested)
                {
                    IsMonitoring  = true;
                    MonitorStatus = $"Resumed monitoring — next check in {intervalMin} min";
                    NotifyChange();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                MonitorStatus = $"Error: {ex.Message[..Math.Min(60, ex.Message.Length)]}";
                NotifyChange();
                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
        }

        IsMonitoring  = false;
        MonitorStatus = "";
        MonitoringUrl = null;
        NotifyChange();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Fingerprint helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A fingerprint is a sorted pipe-separated string of "METHOD /path" labels.
    /// If the spec changes (new/removed endpoint) the fingerprint changes.
    /// </summary>
    private static async Task<string> GetFingerprintAsync(
        SwaggerParser parser, string url, CancellationToken ct)
    {
        var result = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? await parser.ParseUrlAsync(url, ct)
            : await parser.ParseFileAsync(url, ct);

        return BuildFingerprint(result);
    }

    private static async Task<(string fingerprint, SwaggerParseResult result)> GetFingerprintWithResultAsync(
        SwaggerParser parser, string url, CancellationToken ct)
    {
        var result = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? await parser.ParseUrlAsync(url, ct)
            : await parser.ParseFileAsync(url, ct);

        return (BuildFingerprint(result), result);
    }

    private static string BuildFingerprint(SwaggerParseResult result) =>
        string.Join("|", result.Endpoints.Select(e => e.Label).OrderBy(x => x));

    private static HashSet<string> FingerprintToSet(string fingerprint) =>
        fingerprint.Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    // ─────────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void AddLog(string message, string cssClass = "")
    {
        var entry = new LogEntry(DateTime.Now.ToString("HH:mm:ss"), message, cssClass);
        lock (_lock) { Logs.Add(entry); }
    }

    private void NotifyChange() => OnChange?.Invoke();
}
