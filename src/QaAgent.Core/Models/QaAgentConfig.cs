using System.Text.Json.Serialization;

namespace QaAgent.Core.Models;

/// <summary>
/// Root configuration loaded from qa-agent.config.json.
/// All values have sensible defaults so the file is optional.
/// </summary>
public sealed class QaAgentConfig
{
    [JsonPropertyName("ollama")]
    public OllamaConfig Ollama { get; set; } = new();

    [JsonPropertyName("execution")]
    public ExecutionConfig Execution { get; set; } = new();

    [JsonPropertyName("output")]
    public OutputConfig Output { get; set; } = new();

    [JsonPropertyName("auth")]
    public AuthConfig Auth { get; set; } = new();
}

/// <summary>
/// Ollama / LLM connection settings.
/// </summary>
public sealed class OllamaConfig
{
    /// <summary>Base URL of the Ollama server.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "http://localhost:11434";

    /// <summary>Model name to use for generation and healing.</summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "llama3.1";

    /// <summary>LLM temperature for test generation (0 = deterministic, 1 = creative).</summary>
    [JsonPropertyName("temperature")]
    public float Temperature { get; set; } = 0.3f;

    /// <summary>Maximum token count for a single LLM response.</summary>
    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; } = 4096;
}

/// <summary>
/// Test generation and execution settings.
/// </summary>
public sealed class ExecutionConfig
{
    /// <summary>
    /// How many endpoints to test per run.
    /// Set to 0 to test all endpoints.
    /// </summary>
    [JsonPropertyName("maxEndpoints")]
    public int MaxEndpoints { get; set; } = 5;

    /// <summary>Per-test timeout in seconds.</summary>
    [JsonPropertyName("testTimeoutSeconds")]
    public int TestTimeoutSeconds { get; set; } = 15;

    /// <summary>Maximum self-healing attempts when compile fails.</summary>
    [JsonPropertyName("selfHealingAttempts")]
    public int SelfHealingAttempts { get; set; } = 3;

    /// <summary>
    /// Maximum self-healing attempts when tests fail at runtime (assertion failures).
    /// Set to 0 to disable assertion healing.
    /// </summary>
    [JsonPropertyName("assertionHealingAttempts")]
    public int AssertionHealingAttempts { get; set; } = 2;

    /// <summary>Maximum number of endpoint groups compiled/run in parallel.</summary>
    [JsonPropertyName("maxParallelGroups")]
    public int MaxParallelGroups { get; set; } = 4;

    /// <summary>Maximum number of test methods run in parallel within one class.</summary>
    [JsonPropertyName("maxParallelMethods")]
    public int MaxParallelMethods { get; set; } = 5;
}

/// <summary>
/// Authentication credentials injected into generated test code.
/// </summary>
public sealed class AuthConfig
{
    /// <summary>
    /// Auth type: "none" | "bearer" | "apiKey" | "basic"
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "none";

    /// <summary>Bearer token value (without "Bearer " prefix).</summary>
    [JsonPropertyName("bearerToken")]
    public string? BearerToken { get; set; }

    /// <summary>Header name for API Key auth (e.g. "X-API-Key").</summary>
    [JsonPropertyName("apiKeyHeader")]
    public string ApiKeyHeader { get; set; } = "X-API-Key";

    /// <summary>API Key value.</summary>
    [JsonPropertyName("apiKeyValue")]
    public string? ApiKeyValue { get; set; }

    /// <summary>Username for Basic auth.</summary>
    [JsonPropertyName("basicUsername")]
    public string? BasicUsername { get; set; }

    /// <summary>Password for Basic auth.</summary>
    [JsonPropertyName("basicPassword")]
    public string? BasicPassword { get; set; }

    /// <summary>Returns true when auth is actually configured.</summary>
    [JsonIgnore]
    public bool IsConfigured => Type switch
    {
        "bearer"  => !string.IsNullOrWhiteSpace(BearerToken),
        "apiKey"  => !string.IsNullOrWhiteSpace(ApiKeyValue),
        "basic"   => !string.IsNullOrWhiteSpace(BasicUsername),
        _         => false
    };

    /// <summary>
    /// Returns the exact C# line(s) that add the auth header to an HttpRequestMessage.
    /// Used by TestGenerator to inject into the prompt.
    /// </summary>
    [JsonIgnore]
    public string CSharpHeaderCode => Type switch
    {
        "bearer" => $"request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(\"Bearer\", \"{BearerToken}\");",
        "apiKey" => $"request.Headers.Add(\"{ApiKeyHeader}\", \"{ApiKeyValue}\");",
        "basic"  => $"request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(\"Basic\", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(\"{BasicUsername}:{BasicPassword}\")));",
        _        => ""
    };
}

/// <summary>
/// Output / storage settings.
/// </summary>
public sealed class OutputConfig
{
    /// <summary>Whether to save generated .cs files to disk for inspection.</summary>
    [JsonPropertyName("saveGeneratedTests")]
    public bool SaveGeneratedTests { get; set; } = true;

    /// <summary>Directory for run history JSON files (relative to cwd).</summary>
    [JsonPropertyName("historyDir")]
    public string HistoryDir { get; set; } = ".qa-history";

    /// <summary>Directory where Markdown reports are written (relative to cwd).</summary>
    [JsonPropertyName("reportDir")]
    public string ReportDir { get; set; } = ".";
}
