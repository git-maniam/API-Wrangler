namespace ApiWrangler.Domain;

public sealed record CapturedTrafficRecord(
    string Id,
    string ScenarioName,
    DateTimeOffset Timestamp,
    string Method,
    string Url,
    string Origin,
    string Path,
    string? Query = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Body = null,
    int ResponseStatusCode = 200,
    IReadOnlyDictionary<string, string>? ResponseHeaders = null,
    string? ResponseBody = null,
    double DurationMs = 0,
    bool IsCached = false,
    bool IsServiceWorker = false,
    bool IsWebSocket = false,
    bool IsTruncated = false
);

public sealed record TrafficMatchResult(
    bool IsMatched,
    string? MatchedRequestId,
    string? MatchedRequestName,
    string? MatchedCollectionId,
    int SpecificityScore,
    bool IsAmbiguous = false,
    IReadOnlyList<string>? AmbiguousCandidates = null,
    IReadOnlyList<string>? Explanations = null
);

public sealed record TrafficAssertionRule(
    string RuleId,
    string TargetProperty, // e.g. "StatusCode", "Header.X-Custom", "Body.pricing.discount"
    string Operator, // "Equals", "NotEquals", "GreaterThan", "LessThan", "Contains", "NotEmpty", "Regex"
    string ExpectedValue
);

public sealed record CapturedTrafficValidationResult(
    CapturedTrafficRecord TrafficRecord,
    TrafficMatchResult Match,
    bool AssertionsPassed,
    IReadOnlyList<string> FailedAssertions
);

public sealed record DependencyEdge(
    string Caller,
    string Callee,
    int CallCount
);

public sealed record TrafficDependencyMap(
    string ScenarioName,
    IReadOnlyList<string> Endpoints,
    IReadOnlyList<DependencyEdge> Edges
);

public sealed record PlaywrightCombinedScenarioResult(
    string ScenarioName,
    bool UiPassed,
    bool ApiPassed,
    bool CombinedPassed,
    string FailureSource, // "None", "UI", "API", "Both"
    IReadOnlyList<CapturedTrafficValidationResult> ValidationResults,
    IReadOnlyList<CapturedTrafficRecord> UnmatchedTraffic,
    IReadOnlyList<CapturedTrafficRecord> AmbiguousTraffic,
    IReadOnlyList<string> LimitationsEncountered,
    string? PrimaryFailureMessage = null
);

public sealed record PlaywrightSummaryReport(
    int TotalScenarios,
    int PassedScenarios,
    int FailedScenarios,
    IReadOnlyList<string> FailingScenarios,
    int TotalCapturedRequests,
    int MatchedRequests,
    int UnmatchedRequests,
    int AmbiguousRequests,
    int TotalAssertionFailures,
    IReadOnlyList<PlaywrightCombinedScenarioResult> Scenarios
);

