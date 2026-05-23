namespace QaAgent.Core.Models;

/// <summary>
/// Represents a single generated test scenario before it is turned into C# source code.
/// The TestGenerator produces a list of these from a given ApiEndpoint.
/// </summary>
public class TestCase
{
    /// <summary>Unique identifier within the current agent session.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>The endpoint this test targets.</summary>
    public required ApiEndpoint Endpoint { get; init; }

    /// <summary>
    /// Human-readable scenario name, e.g. "Returns 200 with valid id",
    /// "Returns 404 when user does not exist".
    /// </summary>
    public required string ScenarioName { get; init; }

    /// <summary>
    /// Categorises the test intent so the generator can pick appropriate assertions.
    /// </summary>
    public required TestScenarioType ScenarioType { get; init; }

    /// <summary>HTTP status code the test expects, e.g. 200, 201, 400, 404.</summary>
    public int ExpectedStatusCode { get; init; }

    /// <summary>
    /// Concrete parameter values to use in the request, keyed by parameter name.
    /// The generator fills these with realistic examples or boundary values.
    /// </summary>
    public Dictionary<string, object?> InputValues { get; init; } = [];

    /// <summary>Optional free-text notes for the LLM, e.g. "use an expired JWT token".</summary>
    public string? Notes { get; init; }

    /// <summary>
    /// The C# source code generated for this test case (populated by TestGenerator).
    /// Null until generation is complete.
    /// </summary>
    public string? GeneratedCode { get; set; }

    /// <summary>Generation attempt counter — incremented by SelfHealingAgent on each retry.</summary>
    public int GenerationAttempt { get; set; } = 1;

    public override string ToString() =>
        $"[{Id}] {Endpoint.Label} — {ScenarioName} (expect {ExpectedStatusCode})";
}

/// <summary>
/// High-level category of a test scenario.
/// Drives which assertions and input values the TestGenerator produces.
/// </summary>
public enum TestScenarioType
{
    /// <summary>Valid inputs, expects the happy-path response (2xx).</summary>
    HappyPath,

    /// <summary>Missing or empty required parameters (expects 400/422).</summary>
    MissingRequired,

    /// <summary>Values at min/max boundaries or just outside them.</summary>
    BoundaryValue,

    /// <summary>Wrong data type or malformed values (expects 400).</summary>
    InvalidType,

    /// <summary>Request without required auth (expects 401/403).</summary>
    Unauthorized,

    /// <summary>Resource that does not exist (expects 404).</summary>
    NotFound,

    /// <summary>Duplicate resource creation (expects 409).</summary>
    Conflict,

    /// <summary>Any other custom scenario.</summary>
    Custom
}