namespace ApiWrangler.Domain;

public sealed record MockRule(
    string Id,
    string Name,
    string Method, // GET, POST, *, etc.
    string PathPattern, // /users/{id}, /orders, etc.
    int Priority = 0, // Higher priority wins
    IReadOnlyDictionary<string, string>? QueryParams = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? BodyPattern = null, // substring or regex pattern
    int ResponseStatusCode = 200,
    IReadOnlyDictionary<string, string>? ResponseHeaders = null,
    string? ResponseBody = null,
    TimeSpan ResponseDelay = default,
    MockFaultType FaultType = MockFaultType.None,
    double IntermittentFailureRate = 0.0, // 0.0 - 1.0
    int? IntermittentSeed = null,
    string? ScenarioStateRequired = null,
    string? ScenarioStateTransition = null
);

public enum MockFaultType
{
    None,
    HttpError,
    DelayedResponse,
    ConnectionTermination,
    MalformedPayload,
    RateLimit
}

public sealed record MockServerLogEntry(
    DateTimeOffset Timestamp,
    string Method,
    string Path,
    string QueryString,
    IReadOnlyDictionary<string, string> Headers,
    string? Body,
    int MatchedRuleCount,
    string? MatchedRuleId,
    int ResponseStatusCode,
    string? FaultApplied,
    TimeSpan Duration
);

public sealed record MockServerConfiguration(
    string Host = "127.0.0.1",
    int Port = 0, // 0 = dynamic
    bool AllowExternalBinding = false,
    string? DefaultNotFoundMessage = "No matching mock rule found.",
    string? AuthToken = null,
    string? AdminApiKey = null,
    bool RequireAuthentication = false,
    int MaxLogRetention = 1000
);

public sealed record HostedMockDeploymentStatus(
    bool IsListening,
    string? ListeningUrl,
    int ActiveRulesCount,
    int RecordedLogCount,
    bool ExternalBindingEnabled,
    bool AuthenticationEnforced
);

public sealed record MockMatchResult(
    bool IsMatched,
    MockRule? Rule,
    int SpecificityScore,
    IReadOnlyList<string> MatchExplanations,
    bool IsAmbiguous = false,
    IReadOnlyList<MockRule>? AmbiguousCandidates = null
);

