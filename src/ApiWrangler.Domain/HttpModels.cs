namespace ApiWrangler.Domain;

public enum ExecutionOutcome { Passed, Failed, Error, Skipped, Cancelled, TimedOut, NotRun }

public sealed record HeaderEntry(string Name, string Value, bool Enabled = true);
public sealed record QueryParamEntry(string Name, string Value, bool Enabled = true);
public sealed record FormDataEntry(string Name, string Value, string? FilePath = null, string? ContentType = null, bool Enabled = true);
public sealed record CookieEntry(string Name, string Value, string? Domain = null, string? Path = null, DateTimeOffset? Expires = null, bool HttpOnly = false, bool Secure = false);

public sealed record RequestBodyDefinition
{
    public string Mode { get; init; } = "raw";
    public string? Text { get; init; }
    public string? FilePath { get; init; }
    public IReadOnlyList<FormDataEntry> FormData { get; init; } = [];
}

public sealed record HttpTransportOptions
{
    public bool FollowRedirects { get; init; } = false;
    public int MaxRedirects { get; init; } = 10;
    public bool StripAuthOnCrossOriginRedirect { get; init; } = true;
    public bool DangerousIgnoreTlsErrors { get; init; } = false;
    public Version? PreferredHttpVersion { get; init; } = null;
    public bool EnableCookieJar { get; init; } = false;
}

public sealed record HttpRequestDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Untitled request";
    public string? Description { get; init; }
    public Guid? CollectionId { get; init; }
    public Guid? FolderId { get; init; }
    public string Protocol { get; init; } = "http";
    public string Method { get; init; } = "GET";
    public string Url { get; init; } = "http://localhost:5057/echo";
    public IReadOnlyList<QueryParamEntry> Query { get; init; } = [];
    public IReadOnlyList<HeaderEntry> Headers { get; init; } = [];
    public RequestBodyDefinition? BodyDefinition { get; init; }
    public string? Body
    {
        get => BodyDefinition?.Text;
        init => BodyDefinition = value is null ? null : new RequestBodyDefinition { Mode = "raw", Text = value };
    }
    public string ContentType { get; init; } = "application/json";
    public int TimeoutMs { get; init; } = 30_000;
    public HttpTransportOptions TransportOptions { get; init; } = new();
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
    string? Error,
    IReadOnlyList<CookieEntry>? Cookies = null,
    string? EffectiveUrl = null,
    int RedirectCount = 0);
