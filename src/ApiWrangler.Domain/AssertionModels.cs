using System.Text.Json.Nodes;

namespace ApiWrangler.Domain;

public sealed record RequestAssertionDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string? Name { get; init; }
    public string Type { get; init; } = "status"; // status, header, cookie, bodyContains, jsonPath, responseTime, responseSize, regex
    public string? Target { get; init; } // header name, cookie name, json path property
    public string Operator { get; init; } = "equals"; // equals, notEquals, contains, notContains, greaterThan, lessThan, exists, notExists, matches
    public JsonNode? Expected { get; init; }
}

public sealed record AssertionResult(
    Guid AssertionId,
    string Name,
    bool Passed,
    string? ActualValue,
    string? ExpectedValue,
    string? Message);

