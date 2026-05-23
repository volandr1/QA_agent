namespace QaAgent.Core.Models;

/// <summary>
/// The outcome of executing a single <see cref="TestCase"/>.
/// Produced by CodeExecutor and consumed by ResultAnalyzer and ReportGenerator.
/// </summary>
public class TestResult
{
    /// <summary>The test case that was executed.</summary>
    public required TestCase TestCase { get; init; }

    /// <summary>Final execution status.</summary>
    public required TestStatus Status { get; init; }

    /// <summary>Actual HTTP status code returned by the API, if the request was made.</summary>
    public int? ActualStatusCode { get; init; }

    /// <summary>Raw response body (truncated to 4 KB for storage).</summary>
    public string? ResponseBody { get; init; }

    /// <summary>Compilation or runtime error message, if any.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Full exception stack trace, if available.</summary>
    public string? StackTrace { get; init; }

    /// <summary>Wall-clock time taken to run the test.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>When the test was executed (UTC).</summary>
    public DateTime ExecutedAt { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// True when the actual status code differs from the expected one —
    /// a strong signal of an API contract violation (potential bug).
    /// </summary>
    public bool IsContractViolation =>
        ActualStatusCode.HasValue &&
        ActualStatusCode.Value != TestCase.ExpectedStatusCode;

    public override string ToString() =>
        $"{Status} | {TestCase.Endpoint.Label} — {TestCase.ScenarioName}" +
        (ActualStatusCode.HasValue ? $" (HTTP {ActualStatusCode})" : "");
}

/// <summary>Execution outcome of a test case.</summary>
public enum TestStatus
{
    /// <summary>Test ran and all assertions passed.</summary>
    Passed,

    /// <summary>Test ran but at least one assertion failed.</summary>
    Failed,

    /// <summary>Code did not compile or threw an unexpected exception before assertions.</summary>
    Error,

    /// <summary>Test was skipped (e.g. missing base URL or auth token).</summary>
    Skipped
}