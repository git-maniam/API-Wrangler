namespace ApiWrangler.Domain;

public enum ExecutionOutcome { Passed, Failed, Error, Skipped, Cancelled, TimedOut, NotRun }

public sealed record HeaderEntry(string Name, string Value, bool Enabled = true);

public sealed record HttpRequestDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Untitled request";
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = "http://localhost:5057/echo";
    public IReadOnlyList<HeaderEntry> Headers { get; init; } = [];
    public string? Body { get; init; }
    public string ContentType { get; init; } = "application/json";
    public int TimeoutMs { get; init; } = 30_000;
}

public sealed record ExecutionLimits
{
    public int PreviewBytes { get; init; } = 5 * 1024 * 1024;
    public long MaxResponseBytes { get; init; } = 100 * 1024 * 1024;
}

// Passed means the transport completed; HTTP status assertions belong to the assertion engine.
public sealed record HttpExecutionResult(
    ExecutionOutcome Outcome,
    int? StatusCode,
    string? ReasonPhrase,
    IReadOnlyList<HeaderEntry> Headers,
    string Preview,
    long BytesReceived,
    bool PreviewTruncated,
    string? BodyPath,
    TimeSpan Duration,
    string? Error);
