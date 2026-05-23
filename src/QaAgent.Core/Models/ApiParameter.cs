namespace QaAgent.Core.Models;

/// <summary>
/// Represents a single parameter of an API endpoint (path, query, header, body).
/// </summary>
public class ApiParameter
{
    /// <summary>Parameter name as defined in the OpenAPI schema.</summary>
    public required string Name { get; init; }

    /// <summary>Where the parameter lives: path | query | header | cookie | body.</summary>
    public required string Location { get; init; }

    /// <summary>OpenAPI type string: string, integer, boolean, array, object, etc.</summary>
    public required string Type { get; init; }

    /// <summary>OpenAPI format hint, e.g. int32, int64, date-time, uuid.</summary>
    public string? Format { get; init; }

    /// <summary>Whether the parameter must be supplied.</summary>
    public bool IsRequired { get; init; }

    /// <summary>Human-readable description from the schema, if present.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// For object/array types: a JSON string representing the example or schema shape.
    /// Passed as context to the LLM so it can generate a realistic request body.
    /// </summary>
    public string? SchemaJson { get; init; }

    /// <summary>Allowed enum values, if the schema restricts the parameter to a fixed set.</summary>
    public IReadOnlyList<string> EnumValues { get; init; } = [];

    public override string ToString() =>
        $"[{Location}] {Name}: {Type}{(Format is null ? "" : $"({Format})")}{(IsRequired ? " *required*" : "")}";
}