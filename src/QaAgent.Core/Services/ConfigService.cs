using System.Text.Json;
using System.Text.Json.Serialization;
using QaAgent.Core.Models;

namespace QA_agent.QaAgent.Core.Services;

/// <summary>
/// Loads and saves qa-agent.config.json from the current working directory.
/// If the file does not exist, a default config is created automatically.
/// </summary>
public static class ConfigService
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Constants
    // ─────────────────────────────────────────────────────────────────────────

    public const string ConfigFileName = "qa-agent.config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase
    };

    // ─────────────────────────────────────────────────────────────────────────
    //  Load
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads config from <paramref name="path"/> (or cwd/<see cref="ConfigFileName"/>).
    /// If the file is missing, writes a default config and returns defaults.
    /// </summary>
    public static async Task<QaAgentConfig> LoadAsync(string? path = null)
    {
        path ??= Path.Combine(Directory.GetCurrentDirectory(), ConfigFileName);

        if (!File.Exists(path))
        {
            var defaults = new QaAgentConfig();
            await SaveAsync(defaults, path);

            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"[Config] Created default config: {path}");
            Console.WriteLine($"[Config] Edit it to change model, timeouts, parallelism, etc.");
            Console.ResetColor();

            return defaults;
        }

        try
        {
            var json   = await File.ReadAllTextAsync(path);
            var config = JsonSerializer.Deserialize<QaAgentConfig>(json, JsonOptions)
                         ?? new QaAgentConfig();

            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.WriteLine($"[Config] Loaded: {path}");
            Console.ResetColor();

            return config;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[Config] WARNING: Could not parse {path}: {ex.Message}");
            Console.WriteLine($"[Config] Using default configuration.");
            Console.ResetColor();

            return new QaAgentConfig();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Save
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Serializes <paramref name="config"/> to <paramref name="path"/>.
    /// </summary>
    public static async Task SaveAsync(QaAgentConfig config, string? path = null)
    {
        path ??= Path.Combine(Directory.GetCurrentDirectory(), ConfigFileName);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Print summary
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Prints the active configuration values to the console.
    /// </summary>
    public static void PrintSummary(QaAgentConfig cfg)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  Ollama    : {cfg.Ollama.Url}  model={cfg.Ollama.Model}  temp={cfg.Ollama.Temperature}  maxTokens={cfg.Ollama.MaxTokens}");
        Console.WriteLine($"  Endpoints : max {(cfg.Execution.MaxEndpoints == 0 ? "ALL" : cfg.Execution.MaxEndpoints)}  timeout={cfg.Execution.TestTimeoutSeconds}s");
        Console.WriteLine($"  Healing   : {cfg.Execution.SelfHealingAttempts} compile + {cfg.Execution.AssertionHealingAttempts} assertion attempts");
        Console.WriteLine($"  Parallel  : {cfg.Execution.MaxParallelGroups} groups × {cfg.Execution.MaxParallelMethods} methods");
        Console.WriteLine($"  History   : {cfg.Output.HistoryDir}  reports={cfg.Output.ReportDir}");
        Console.WriteLine($"  Auth      : {cfg.Auth.Type}{(cfg.Auth.IsConfigured ? " ✓" : " (not configured)")}");

        Console.ResetColor();
    }
}
