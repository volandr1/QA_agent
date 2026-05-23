using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QA_agent.QaAgent.Core.Services;

/// <summary>
/// Lightweight client for local Ollama API.
/// Compatible with Ollama's /api/chat endpoint.
/// No API key needed — runs fully locally.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Constants
    // ─────────────────────────────────────────────────────────────────────────

    private const string DefaultBaseUrl = "http://localhost:11434";
    private const string DefaultModel   = "llama3.1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy   = JsonNamingPolicy.CamelCase
    };

    // ─────────────────────────────────────────────────────────────────────────
    //  Fields
    // ─────────────────────────────────────────────────────────────────────────

    private readonly HttpClient _http;
    private readonly string     _model;
    private readonly string     _baseUrl;

    private readonly List<OllamaMessage> _history = [];

    // ─────────────────────────────────────────────────────────────────────────
    //  Constructor
    // ─────────────────────────────────────────────────────────────────────────

    public OllamaClient(string model = DefaultModel, string baseUrl = DefaultBaseUrl)
    {
        _model   = model;
        _baseUrl = baseUrl.TrimEnd('/');
        _http    = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One-shot generation — does NOT store history.
    /// Use for test code generation.
    /// </summary>
    public async Task<string> GenerateAsync(
        string prompt,
        string? systemPrompt  = null,
        float   temperature   = 0.2f,
        int     maxTokens     = 8192,
        CancellationToken ct  = default)
    {
        var messages = new List<OllamaMessage>();

        if (systemPrompt is not null)
            messages.Add(new OllamaMessage("system", systemPrompt));

        messages.Add(new OllamaMessage("user", prompt));

        return await SendAsync(messages, temperature, maxTokens, ct);
    }

    /// <summary>
    /// Multi-turn chat with history — use for agent loop.
    /// </summary>
    public async Task<string> ChatAsync(
        string userMessage,
        string? systemPrompt  = null,
        float   temperature   = 0.3f,
        int     maxTokens     = 8192,
        CancellationToken ct  = default)
    {
        if (_history.Count == 0 && systemPrompt is not null)
            _history.Add(new OllamaMessage("system", systemPrompt));

        _history.Add(new OllamaMessage("user", userMessage));

        var reply = await SendAsync(_history, temperature, maxTokens, ct);

        _history.Add(new OllamaMessage("assistant", reply));

        return reply;
    }

    /// <summary>Clears conversation history.</summary>
    public void ClearHistory() => _history.Clear();

    /// <summary>Checks if Ollama is running and the model is available.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
            if (!response.IsSuccessStatusCode) return false;

            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Contains(_model.Split(':')[0]); // match by base name
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();

    // ─────────────────────────────────────────────────────────────────────────
    //  Core send
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<string> SendAsync(
        IEnumerable<OllamaMessage> messages,
        float temperature,
        int maxTokens,
        CancellationToken ct)
    {
        var request = new OllamaChatRequest
        {
            Model    = _model,
            Messages = messages.ToList(),
            Stream   = false,
            Options  = new OllamaOptions
            {
                Temperature = temperature,
                NumPredict  = maxTokens
            }
        };

        var json        = JsonSerializer.Serialize(request, JsonOptions);
        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        Log($"→ Sending to {_model} ({json.Length} chars)");

        var sw           = System.Diagnostics.Stopwatch.StartNew();
        var httpResponse = await _http.PostAsync($"{_baseUrl}/api/chat", httpContent, ct);
        sw.Stop();

        if (!httpResponse.IsSuccessStatusCode)
        {
            var error = await httpResponse.Content.ReadAsStringAsync(ct);
            throw new OllamaException($"Ollama error {(int)httpResponse.StatusCode}: {error}");
        }

        var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
        var parsed       = JsonSerializer.Deserialize<OllamaChatResponse>(responseJson, JsonOptions)
            ?? throw new OllamaException("Failed to deserialize Ollama response.");

        var text = parsed.Message?.Content ?? string.Empty;
        Log($"← Done in {sw.ElapsedMilliseconds}ms ({text.Length} chars)");

        return text;
    }

    private static void Log(string msg)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[OllamaClient] {msg}");
        Console.ResetColor();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  DTOs
// ─────────────────────────────────────────────────────────────────────────────

public record OllamaMessage(
    [property: JsonPropertyName("role")]    string Role,
    [property: JsonPropertyName("content")] string Content);

public class OllamaChatRequest
{
    [JsonPropertyName("model")]    public required string             Model    { get; init; }
    [JsonPropertyName("messages")] public required List<OllamaMessage> Messages { get; init; }
    [JsonPropertyName("stream")]   public bool                        Stream   { get; init; }
    [JsonPropertyName("options")]  public OllamaOptions?              Options  { get; init; }
}

public class OllamaOptions
{
    [JsonPropertyName("temperature")] public float Temperature { get; init; }
    [JsonPropertyName("num_predict")] public int   NumPredict  { get; init; }
}

public class OllamaChatResponse
{
    [JsonPropertyName("message")] public OllamaMessage? Message { get; init; }
    [JsonPropertyName("done")]    public bool            Done    { get; init; }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Exception
// ─────────────────────────────────────────────────────────────────────────────

public class OllamaException(string message, Exception? inner = null)
    : Exception(message, inner);