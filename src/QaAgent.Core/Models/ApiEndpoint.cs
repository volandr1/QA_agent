namespace QaAgent.Core.Models;

/// <summary>
/// A fully resolved description of one API operation extracted from a Swagger / OpenAPI schema.
/// This is the primary input to the TestGenerator.
/// </summary>
public class ApiEndpoint
{
    /// <summary>HTTP method in upper-case: GET, POST, PUT, PATCH, DELETE, etc.</summary>
    public required string Method { get; init; }

    /// <summary>Path template exactly as in the schema, e.g. /users/{id}.</summary>
    public required string Path { get; init; }

    /// <summary>Operation ID from the schema, e.g. "getUserById". Used to name test classes.</summary>
    public string? OperationId { get; init; }

    /// <summary>Short summary / description from the schema.</summary>
    public string? Summary { get; init; }

    /// <summary>All parameters: path, query, header, and the request body (location = "body").</summary>
    public IReadOnlyList<ApiParameter> Parameters { get; init; } = [];

    /// <summary>
    /// Map of HTTP status codes to their description, e.g. { "200": "Success", "404": "Not found" }.
    /// The TestGenerator uses this to produce assertions for each expected response.
    /// </summary>
    public IReadOnlyDictionary<string, string> Responses { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Tags from the schema, useful for grouping tests by feature area.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>True when the operation requires at least one security scheme.</summary>
    public bool RequiresAuth { get; init; }

    /// <summary>The media type of the request body, e.g. "application/json".</summary>
    public string? RequestBodyMediaType { get; init; }

    /// <summary>Human-readable label used in log output and report headers.</summary>
    public string Label => $"{Method} {Path}";

    public override string ToString() => Label;
}