using System.Text.Json;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;
using QaAgent.Core.Models;

namespace QA_agent.QaAgent.Core.Tools;

/// <summary>
/// Parses a Swagger / OpenAPI 2.0 or 3.x document (from a file path, raw JSON/YAML string,
/// or a URL) and returns a flat list of <see cref="ApiEndpoint"/> objects ready for the
/// TestGenerator.
/// </summary>
public sealed class SwaggerParser
{
    // -------------------------------------------------------------------------
    // Public surface
    // -------------------------------------------------------------------------

    /// <summary>Parse from a local file path (.json or .yaml).</summary>
    public Task<SwaggerParseResult> ParseFileAsync(string filePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("OpenAPI file not found.", filePath);

        using var stream = File.OpenRead(filePath);
        return Task.FromResult(ParseStream(stream, filePath));
    }

    /// <summary>Parse from a raw JSON or YAML string.</summary>
    public Task<SwaggerParseResult> ParseStringAsync(string content,
        CancellationToken ct = default)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        return Task.FromResult(ParseStream(stream, "<string>"));
    }

    /// <summary>Download the schema from a URL and parse it.</summary>
    public async Task<SwaggerParseResult> ParseUrlAsync(string url,
        CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return ParseStream(stream, url);
    }

    // -------------------------------------------------------------------------
    // Core parsing logic
    // -------------------------------------------------------------------------

    private static SwaggerParseResult ParseStream(Stream stream, string source)
    {
        var reader = new OpenApiStreamReader();
        var document = reader.Read(stream, out var diagnostic);

        var warnings = diagnostic.Warnings
            .Select(w => w.Message)
            .ToList();

        var errors = diagnostic.Errors
            .Select(e => e.Message)
            .ToList();

        if (document is null)
            return new SwaggerParseResult(source, [], errors, warnings);

        var endpoints = ExtractEndpoints(document);

        return new SwaggerParseResult(source, endpoints, errors, warnings)
        {
            ApiTitle = document.Info?.Title,
            ApiVersion = document.Info?.Version,
            BaseUrl = document.Servers?.FirstOrDefault()?.Url
        };
    }

    private static List<ApiEndpoint> ExtractEndpoints(OpenApiDocument doc)
    {
        var endpoints = new List<ApiEndpoint>();

        foreach (var (pathTemplate, pathItem) in doc.Paths)
        {
            foreach (var (operationType, operation) in pathItem.Operations)
            {
                var method = operationType.ToString().ToUpperInvariant();
                var parameters = BuildParameters(operation, pathItem);
                var responses = BuildResponses(operation);

                endpoints.Add(new ApiEndpoint
                {
                    Method = method,
                    Path = pathTemplate,
                    OperationId = operation.OperationId,
                    Summary = operation.Summary ?? operation.Description,
                    Parameters = parameters,
                    Responses = responses,
                    Tags = operation.Tags?.Select(t => t.Name).ToList() ?? [],
                    RequiresAuth = operation.Security?.Count > 0 ||
                                   (doc.SecurityRequirements?.Count > 0 &&
                                    (operation.Security == null || operation.Security.Count == 0)),
                    RequestBodyMediaType = operation.RequestBody?.Content?.Keys.FirstOrDefault()
                });
            }
        }

        return endpoints;
    }

    // -------------------------------------------------------------------------
    // Parameter extraction
    // -------------------------------------------------------------------------

    private static List<ApiParameter> BuildParameters(
        OpenApiOperation operation,
        OpenApiPathItem pathItem)
    {
        var result = new List<ApiParameter>();

        // Path-level parameters merged with operation-level (operation wins on name clash)
        var allParams = pathItem.Parameters
            .Concat(operation.Parameters)
            .GroupBy(p => p.Name)
            .Select(g => g.Last())
            .ToList();

        foreach (var p in allParams)
        {
            result.Add(new ApiParameter
            {
                Name = p.Name,
                Location = p.In?.ToString().ToLowerInvariant() ?? "query",
                Type = p.Schema?.Type ?? "string",
                Format = p.Schema?.Format,
                IsRequired = p.Required,
                Description = p.Description,
                SchemaJson = SerializeSchema(p.Schema),
                EnumValues = p.Schema?.Enum?
                    .Select(e => e.ToString() ?? "")
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList() ?? []
            });
        }

        // Request body as a virtual "body" parameter
        if (operation.RequestBody is { } body)
        {
            var (mediaType, content) = body.Content.FirstOrDefault();
            if (content?.Schema is { } schema)
            {
                result.Add(new ApiParameter
                {
                    Name = "body",
                    Location = "body",
                    Type = schema.Type ?? "object",
                    Format = schema.Format,
                    IsRequired = body.Required,
                    Description = body.Description,
                    SchemaJson = SerializeSchema(schema)
                });
            }
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Response extraction
    // -------------------------------------------------------------------------

    private static Dictionary<string, string> BuildResponses(OpenApiOperation operation)
    {
        var result = new Dictionary<string, string>();

        foreach (var (statusCode, response) in operation.Responses)
        {
            result[statusCode] = response.Description ?? statusCode;
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Schema serialisation helper
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts an OpenApiSchema to a compact JSON summary string that we pass to
    /// the LLM as context.  We only capture type, properties, and example — not
    /// the full recursive schema — to keep the prompt concise.
    /// </summary>
    private static string? SerializeSchema(OpenApiSchema? schema)
    {
        if (schema is null) return null;

        try
        {
            var summary = new Dictionary<string, object?>();

            if (!string.IsNullOrEmpty(schema.Type))
                summary["type"] = schema.Type;

            if (schema.Properties?.Count > 0)
            {
                summary["properties"] = schema.Properties
                    .ToDictionary(
                        kvp => kvp.Key,
                        kvp => (object?)(kvp.Value.Type ?? "any"));
            }

            if (schema.Items is not null)
                summary["items"] = schema.Items.Type ?? "any";

            if (schema.Example is not null)
                summary["example"] = schema.Example.ToString();

            if (schema.Enum?.Count > 0)
                summary["enum"] = schema.Enum.Select(e => e.ToString()).ToList();

            return JsonSerializer.Serialize(summary);
        }
        catch
        {
            return null;
        }
    }
}

// -------------------------------------------------------------------------
// Result wrapper
// -------------------------------------------------------------------------

/// <summary>
/// The result of a successful (or partially failed) schema parse.
/// </summary>
public sealed class SwaggerParseResult
{
    public SwaggerParseResult(
        string source,
        IReadOnlyList<ApiEndpoint> endpoints,
        IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings)
    {
        Source = source;
        Endpoints = endpoints;
        Errors = errors;
        Warnings = warnings;
    }

    /// <summary>Source identifier (file path, URL, or "&lt;string&gt;").</summary>
    public string Source { get; }

    /// <summary>All extracted endpoints. Empty on parse failure.</summary>
    public IReadOnlyList<ApiEndpoint> Endpoints { get; }

    /// <summary>Fatal parse errors (schema could not be fully read).</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Non-fatal warnings (schema may still be usable).</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>API title from the Info object.</summary>
    public string? ApiTitle { get; init; }

    /// <summary>API version from the Info object.</summary>
    public string? ApiVersion { get; init; }

    /// <summary>First server URL from the Servers list.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>True when at least one endpoint was found (schema errors are treated as warnings).</summary>
    public bool IsSuccess => Endpoints.Count > 0;

    /// <summary>Prints a short summary to the console.</summary>
    public void PrintSummary()
    {
        Console.WriteLine($"[SwaggerParser] Source  : {Source}");
        Console.WriteLine($"[SwaggerParser] API     : {ApiTitle} v{ApiVersion}");
        Console.WriteLine($"[SwaggerParser] Base URL: {BaseUrl ?? "(none)"}");
        Console.WriteLine($"[SwaggerParser] Endpoints found: {Endpoints.Count}");

        if (Errors.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            foreach (var e in Errors) Console.WriteLine($"  ERROR: {e}");
            Console.ResetColor();
        }

        if (Warnings.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            foreach (var w in Warnings) Console.WriteLine($"  WARN : {w}");
            Console.ResetColor();
        }

        Console.WriteLine();
        foreach (var ep in Endpoints)
        {
            var auth = ep.RequiresAuth ? " [auth]" : "";
            Console.WriteLine($"  {ep.Method,-7} {ep.Path}{auth}");
        }
    }
}