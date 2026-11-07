namespace ApiWrangler.Domain;

public enum VariableSource
{
    ProjectDefault = 0,
    Collection = 1,
    Environment = 2,
    LocalEnvironment = 3,
    RunOverride = 4,
    Dataset = 5,
    RequestLocal = 6,
    Secret = 7,
    Dynamic = 8
}

public sealed record VariableEntry(string Name, string Value, VariableSource Source, bool IsSecret = false);

public sealed record DynamicValueContext
{
    public int Seed { get; init; } = 42;
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string RunId { get; init; } = Guid.NewGuid().ToString("N");
    public int IterationIndex { get; init; } = 0;
}

public sealed record ResolvedVariableInspection(string Name, string RedactedValue, VariableSource Source, bool IsSecret);

public sealed class VariableResolutionException(string variableName, string location, string reason)
    : Exception($"Variable resolution failed for '{variableName}' at '{location}': {reason}")
{
    public string VariableName { get; } = variableName;
    public string Location { get; } = location;
    public string Reason { get; } = reason;
}

