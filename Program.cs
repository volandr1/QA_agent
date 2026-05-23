using QA_agent.QaAgent.Core.Agents;
using QA_agent.QaAgent.Core.Services;
using QA_agent.QaAgent.Core.Tools;
using QaAgent.Core.Models;
using QA_agent.Web;
using HtmlReportGenerator = QA_agent.QaAgent.Core.Services.HtmlReportGenerator;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Default command: launch web UI (so pressing Run in IDE works without arguments)
if (args.Length == 0) args = ["web"];

var command = args[0].ToLowerInvariant();

// ─────────────────────────────────────────────────────────────────────────────
//  parse <path-or-url>
// ─────────────────────────────────────────────────────────────────────────────
if (command == "parse")
{
    if (args.Length < 2) { Console.WriteLine("Usage: qaagent parse <path-or-url>"); return 1; }
    var parser = new SwaggerParser();
    Console.WriteLine("═══════════════════════════════════════════");
    Console.WriteLine("  QA Agent — Swagger Parser");
    Console.WriteLine("═══════════════════════════════════════════");
    var result = args[1].StartsWith("http", StringComparison.OrdinalIgnoreCase)
        ? await parser.ParseUrlAsync(args[1])
        : await parser.ParseFileAsync(args[1]);
    result.PrintSummary();
    if (!result.IsSuccess) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("\n[FAIL]"); Console.ResetColor(); return 2; }
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n[OK] {result.Endpoints.Count} endpoint(s) ready.");
    Console.ResetColor();
    return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
//  ping-ollama [model] [url]
// ─────────────────────────────────────────────────────────────────────────────
if (command == "ping-ollama")
{
    var cfg   = await ConfigService.LoadAsync();
    var model = args.Length >= 2 ? args[1] : cfg.Ollama.Model;
    var url   = args.Length >= 3 ? args[2] : cfg.Ollama.Url;

    Console.WriteLine("═══════════════════════════════════════════");
    Console.WriteLine("  QA Agent — Ollama Connectivity Test");
    Console.WriteLine("═══════════════════════════════════════════\n");

    using var ollama = new OllamaClient(model, url);
    var isUp = await ollama.PingAsync();
    if (!isUp)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[FAIL] Ollama not reachable at {url} or model '{model}' not found.");
        Console.WriteLine("       Make sure Ollama is running: ollama serve");
        Console.ResetColor();
        return 2;
    }

    try
    {
        var response = await ollama.GenerateAsync("Reply with exactly: OLLAMA_OK", temperature: 0f, maxTokens: 16);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[OK] Ollama responded: {response.Trim()}");
        Console.ResetColor();
        return 0;
    }
    catch (OllamaException ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[FAIL] {ex.Message}");
        Console.ResetColor();
        return 2;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  config — print current configuration
// ─────────────────────────────────────────────────────────────────────────────
if (command == "config")
{
    var cfg = await ConfigService.LoadAsync();
    Console.WriteLine("═══════════════════════════════════════════");
    Console.WriteLine("  QA Agent — Active Configuration");
    Console.WriteLine($"  File: {Path.Combine(Directory.GetCurrentDirectory(), ConfigService.ConfigFileName)}");
    Console.WriteLine("═══════════════════════════════════════════\n");
    ConfigService.PrintSummary(cfg);
    Console.WriteLine();
    return 0;
}

// ─────────────────────────────────────────────────────────────────────────────
//  run-ollama <swagger-url> [base-url] [model]
// ─────────────────────────────────────────────────────────────────────────────
if (command == "run-ollama")
{
    if (args.Length < 2) { Console.WriteLine("Usage: qaagent run-ollama <swagger-url> [base-url] [model]"); return 1; }

    // Load config first — CLI args override config values
    var cfg = await ConfigService.LoadAsync();

    var swaggerSource = args[1];
    var baseUrl       = args.Length >= 3 ? args[2] : "https://petstore3.swagger.io/api/v3";
    var model         = args.Length >= 4 ? args[3] : cfg.Ollama.Model;

    Console.WriteLine("═══════════════════════════════════════════");
    Console.WriteLine($"  QA Agent — Full Run via Ollama ({model})");
    Console.WriteLine("═══════════════════════════════════════════\n");
    ConfigService.PrintSummary(cfg);
    Console.WriteLine();

    // Очищуємо старі згенеровані тести перед новим запуском
    if (cfg.Output.SaveGeneratedTests)
    {
        var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "GeneratedTests");
        if (Directory.Exists(outputDir))
        {
            foreach (var f in Directory.GetFiles(outputDir, "*.cs"))
                File.Delete(f);
        }
    }

    // Step 1: Parse
    Console.WriteLine("[ STEP 1 ] Parsing Swagger schema...");
    var parser = new SwaggerParser();
    var parseResult = swaggerSource.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        ? await parser.ParseUrlAsync(swaggerSource)
        : await parser.ParseFileAsync(swaggerSource);

    if (!parseResult.IsSuccess)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("[FAIL] Could not parse schema.");
        Console.ResetColor();
        return 2;
    }

    // Apply maxEndpoints limit (0 = all)
    var endpointsToTest = cfg.Execution.MaxEndpoints > 0
        ? parseResult.Endpoints.Take(cfg.Execution.MaxEndpoints).ToList()
        : parseResult.Endpoints.ToList();

    Console.WriteLine($"  Found {parseResult.Endpoints.Count} endpoint(s), testing {endpointsToTest.Count}\n");

    // Step 2: Check Ollama
    Console.WriteLine("[ STEP 2 ] Checking Ollama...");
    using var ollama = new OllamaClient(model, cfg.Ollama.Url);
    var isUp = await ollama.PingAsync();
    if (!isUp)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"[FAIL] Ollama not running or model '{model}' not found.");
        Console.WriteLine("       Run: ollama serve");
        Console.ResetColor();
        return 2;
    }
    Console.WriteLine($"  Ollama OK — using {model}\n");

    // Step 3: Generate
    Console.WriteLine("[ STEP 3 ] Generating tests via Ollama...");
    var generator = new TestGenerator(
        ollama,
        baseUrl,
        temperature: cfg.Ollama.Temperature,
        maxTokens:   cfg.Ollama.MaxTokens,
        auth:        cfg.Auth);

    var testCases = await generator.GenerateForAllAsync(endpointsToTest);
    Console.WriteLine($"  Generated {testCases.Count} test case(s)\n");

    // Save generated files (optional)
    if (cfg.Output.SaveGeneratedTests)
    {
        var outputDir = Path.Combine(Directory.GetCurrentDirectory(), "GeneratedTests");
        Directory.CreateDirectory(outputDir);
        var savedFiles = new HashSet<string>();
        foreach (var tc in testCases.Where(t => t.GeneratedCode is not null))
        {
            var className = tc.Endpoint.OperationId
                ?? tc.Endpoint.Method + "_" + tc.Endpoint.Path.Replace("/", "_").Trim('_');
            var fileName = Path.Combine(outputDir, $"{className}Tests.cs");
            if (savedFiles.Add(fileName))
                await File.WriteAllTextAsync(fileName, tc.GeneratedCode!);
        }
    }

    // Step 4: Compile, self-heal, execute
    Console.WriteLine("[ STEP 4 ] Compiling, healing and executing via Roslyn...\n");

    // --break-code: intentionally corrupt code to test self-healing
    if (args.Contains("--break-code"))
    {
        Console.ForegroundColor = ConsoleColor.DarkYellow;
        Console.WriteLine("  [TEST MODE] Injecting compile errors to test self-healing...\n");
        Console.ResetColor();
        foreach (var tc in testCases.Where(t => t.GeneratedCode is not null))
            tc.GeneratedCode = InjectErrors(tc.GeneratedCode!);
    }

    var executor = new CodeExecutor(
        testTimeout:        TimeSpan.FromSeconds(cfg.Execution.TestTimeoutSeconds),
        maxParallelMethods: cfg.Execution.MaxParallelMethods);

    var healer = new SelfHealingAgent(
        ollama,
        executor,
        maxAttempts:          cfg.Execution.SelfHealingAttempts,
        maxConcurrency:       cfg.Execution.MaxParallelGroups,
        maxAssertionAttempts: cfg.Execution.AssertionHealingAttempts);

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var results = await healer.HealAndRunAsync(testCases);
    sw.Stop();

    // Summary
    var passed  = results.Count(r => r.Status == TestStatus.Passed);
    var failed  = results.Count(r => r.Status == TestStatus.Failed);
    var errors  = results.Count(r => r.Status == TestStatus.Error);
    var skipped = results.Count(r => r.Status == TestStatus.Skipped);

    Console.WriteLine("\n═══════════════════════════════════════════");
    Console.WriteLine("  Execution Results");
    Console.WriteLine("═══════════════════════════════════════════\n");

    foreach (var r in results)
    {
        Console.ForegroundColor = r.Status switch
        {
            TestStatus.Passed  => ConsoleColor.Green,
            TestStatus.Skipped => ConsoleColor.Yellow,
            _                  => ConsoleColor.Red
        };
        var icon = r.Status switch
        {
            TestStatus.Passed  => "[PASS]",
            TestStatus.Failed  => "[FAIL]",
            TestStatus.Error   => "[ERR] ",
            TestStatus.Skipped => "[SKIP]",
            _                  => "[????]"
        };
        Console.WriteLine($"{icon} {r.TestCase.ScenarioName} ({r.Duration.TotalMilliseconds:0}ms)");
        if (r.ErrorMessage is not null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"       {r.ErrorMessage[..Math.Min(120, r.ErrorMessage.Length)]}");
        }
        Console.ResetColor();
    }

    Console.WriteLine();
    Console.ForegroundColor = passed  > 0 ? ConsoleColor.Green  : ConsoleColor.White;
    Console.WriteLine($"PASSED : {passed}");
    Console.ForegroundColor = failed  > 0 ? ConsoleColor.Red    : ConsoleColor.White;
    Console.WriteLine($"FAILED : {failed}");
    Console.ForegroundColor = errors  > 0 ? ConsoleColor.Red    : ConsoleColor.White;
    Console.WriteLine($"ERRORS : {errors}");
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"SKIPPED: {skipped}");
    Console.ResetColor();

    // Step 5: Save history + compare + generate report
    Console.WriteLine("\n[ STEP 5 ] Saving history and generating report...");

    var historyDir = Path.IsPathRooted(cfg.Output.HistoryDir)
        ? cfg.Output.HistoryDir
        : Path.Combine(Directory.GetCurrentDirectory(), cfg.Output.HistoryDir);

    var reportSvc  = new ReportService(historyDir);
    var currentRun = await reportSvc.SaveAsync(
        results,
        baseUrl,
        parseResult.ApiTitle,
        $"ollama/{model}",
        sw.Elapsed.TotalSeconds);

    // Порівнюємо з попереднім запуском (якщо є)
    var previousRun  = await reportSvc.GetPreviousRunAsync(baseUrl);
    RunComparison? comparison = null;
    if (previousRun is not null)
    {
        comparison = ReportService.Compare(previousRun, currentRun);
        var delta  = comparison.PassRateDelta;
        var emoji  = delta > 0 ? "📈" : delta < 0 ? "📉" : "➡️";
        Console.ForegroundColor = delta >= 0 ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  {emoji} Pass rate: {previousRun.PassRate}% → {currentRun.PassRate}% ({(delta >= 0 ? "+" : "")}{delta:F1}%)");
        if (comparison.NewFailures.Count > 0)
            Console.WriteLine($"  ⚠ {comparison.NewFailures.Count} нових регресій!");
        if (comparison.FixedTests.Count > 0)
            Console.WriteLine($"  ✅ {comparison.FixedTests.Count} тест(ів) виправлено");
        Console.ResetColor();
    }
    else
    {
        Console.WriteLine("  (Перший запуск — history для порівняння ще немає)");
    }

    var reporter  = new ReportGenerator(ollama);
    var reportDir = Path.GetFullPath(
        Path.IsPathRooted(cfg.Output.ReportDir)
            ? cfg.Output.ReportDir
            : Path.Combine(Directory.GetCurrentDirectory(), cfg.Output.ReportDir));

    Directory.CreateDirectory(reportDir);
    var reportPath = Path.Combine(reportDir, $"qa-report-{DateTime.Now:yyyy-MM-dd-HHmmss}.md");

    var report = await reporter.GenerateAsync(new ReportInput
    {
        Results       = results,
        BaseUrl       = baseUrl,
        ApiTitle      = parseResult.ApiTitle,
        ModelName     = $"ollama/{model}",
        TotalDuration = sw.Elapsed,
        Comparison    = comparison
    }, reportPath);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n[OK] Report saved: {reportPath}");
    Console.ResetColor();

    // Print report to console too
    Console.WriteLine();
    Console.WriteLine(report);

    return (failed + errors) > 0 ? 1 : 0;
}

// ─────────────────────────────────────────────────────────────────────────────
//  web [port]
// ─────────────────────────────────────────────────────────────────────────────
if (command == "web")
{
    var port = args.Length >= 2 && int.TryParse(args[1], out var p) ? p : 5000;
    var cfg  = await ConfigService.LoadAsync();

    // Force Development so UseStaticWebAssets() is active — required to serve
    // the Blazor framework files (_framework/blazor.web.js) from embedded resources.
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args            = Array.Empty<string>(),
        EnvironmentName = "Development"
    });
    builder.WebHost.UseUrls($"http://localhost:{port}");

    builder.Services.AddRazorComponents().AddInteractiveServerComponents();

    var historyDir = Path.GetFullPath(
        Path.IsPathRooted(cfg.Output.HistoryDir)
            ? cfg.Output.HistoryDir
            : Path.Combine(Directory.GetCurrentDirectory(), cfg.Output.HistoryDir));

    builder.Services.AddSingleton(cfg);
    builder.Services.AddSingleton(new ReportService(historyDir));
    builder.Services.AddSingleton<QaRunnerService>();

    var app = builder.Build();
    app.UseStaticFiles();
    app.UseAntiforgery();
    app.MapRazorComponents<QA_agent.Components.App>().AddInteractiveServerRenderMode();

    // HTML report download: GET /run/{id}/report.html
    app.MapGet("/run/{id}/report.html", async (string id, ReportService reportSvc) =>
    {
        var runs = await reportSvc.GetAllRunsAsync();
        var run  = runs.FirstOrDefault(r => r.Id == id);
        if (run is null) return Results.NotFound("Run not found.");

        var html     = HtmlReportGenerator.Generate(run);
        var fileName = $"qa-report-{run.RunAt:yyyy-MM-dd}-{run.Id}.html";
        var bytes    = System.Text.Encoding.UTF8.GetBytes(html);

        return Results.File(bytes, "text/html; charset=utf-8", fileName);
    });

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"QA Agent UI → http://localhost:{port}");
    Console.ResetColor();

    await app.RunAsync();
    return 0;
}

PrintUsage();
return 1;

static void PrintUsage()
{
    Console.WriteLine("QA Agent — Autonomous API Testing\n");
    Console.WriteLine("Commands:");
    Console.WriteLine("  parse       <swagger-url>                             Parse Swagger schema");
    Console.WriteLine("  ping-ollama [model] [ollama-url]                      Test Ollama connection");
    Console.WriteLine("  config                                                 Show active configuration");
    Console.WriteLine("  run-ollama  <swagger-url> [base-url] [model]          Generate + run via Ollama");
    Console.WriteLine("  run-ollama  <swagger-url> [base-url] [model] --break-code   Test self-healing");
    Console.WriteLine("  web         [port]                                           Launch Blazor web UI (default port 5000)\n");
    Console.WriteLine("Config file: qa-agent.config.json (auto-created with defaults on first run)\n");
    Console.WriteLine("Examples:");
    Console.WriteLine("  qaagent parse      https://petstore3.swagger.io/api/v3/openapi.json");
    Console.WriteLine("  qaagent ping-ollama");
    Console.WriteLine("  qaagent config");
    Console.WriteLine("  qaagent run-ollama https://petstore3.swagger.io/api/v3/openapi.json https://petstore3.swagger.io/api/v3");
    Console.WriteLine("  qaagent run-ollama https://petstore3.swagger.io/api/v3/openapi.json https://petstore3.swagger.io/api/v3 llama3.1 --break-code");
}

/// <summary>
/// Injects 3 realistic compile errors into generated C# code to test self-healing.
/// Removes a using directive, breaks a type name, and removes a closing brace.
/// </summary>
static string InjectErrors(string code)
{
    var lines    = code.Split('\n').ToList();
    var modified = new List<string>(lines);
    int injected = 0;

    for (int i = 0; i < modified.Count && injected < 3; i++)
    {
        var line = modified[i];

        // Error 1: Remove "using System.Net.Http;" to break HttpClient
        if (injected == 0 && line.TrimStart().StartsWith("using System.Net.Http;"))
        {
            modified[i] = "// BROKEN: " + line;
            injected++;
            continue;
        }

        // Error 2: Replace "HttpClient" with "HttpClientXXX" (unknown type)
        if (injected == 1 && line.Contains("new HttpClient()"))
        {
            modified[i] = line.Replace("new HttpClient()", "new HttpClientBROKEN()");
            injected++;
            continue;
        }

        // Error 3: Replace "Assert.Equal" with "AssertBROKEN.Equal"
        if (injected == 2 && line.Contains("Assert.Equal"))
        {
            modified[i] = line.Replace("Assert.Equal", "AssertBROKEN.Equal");
            injected++;
            continue;
        }
    }

    Console.ForegroundColor = ConsoleColor.DarkRed;
    Console.WriteLine($"    Injected {injected} error(s) into generated code");
    Console.ResetColor();

    return string.Join("\n", modified);
}
