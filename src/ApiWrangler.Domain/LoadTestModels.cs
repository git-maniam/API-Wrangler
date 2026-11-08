namespace ApiWrangler.Domain;

public enum LoadProfileType
{
    FixedUsers,
    RampUp,
    Spike,
    Step
}

public enum LoadModelType
{
    ClosedLoopVirtualUsers,
    OpenLoopArrivalRate
}

public sealed record LoadTestConfiguration(
    string TargetUrl,
    string Method = "GET",
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Body = null,
    LoadModelType ModelType = LoadModelType.ClosedLoopVirtualUsers,
    LoadProfileType ProfileType = LoadProfileType.FixedUsers,
    int VirtualUsers = 10,
    int MaxVirtualUsers = 50,
    int TargetRatePerSecond = 50,
    TimeSpan Duration = default,
    TimeSpan RampUpDuration = default,
    TimeSpan ThinkTime = default,
    int MaxConsecutiveErrors = 20,
    double MaxErrorRateThreshold = 0.5, // Stop test if error rate exceeds 50%
    bool RequireTargetConfirmation = true,
    bool Confirmed = false
);

public sealed record LoadIterationMetric(
    int IterationId,
    int VirtualUserId,
    long StartTimestampTicks,
    TimeSpan Latency,
    int StatusCode,
    bool Success,
    string? ErrorCategory = null
);

public sealed record LatencyHistogram(
    IReadOnlyList<long> LatenciesMs,
    double MinMs,
    double MeanMs,
    double MaxMs,
    double P50Ms,
    double P90Ms,
    double P95Ms,
    double P99Ms
);

public sealed record LoadTestReport(
    string TargetUrl,
    LoadModelType ModelType,
    LoadProfileType ProfileType,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    TimeSpan TotalDuration,
    int TotalRequests,
    int SuccessfulRequests,
    int FailedRequests,
    double AchievedRps,
    double TargetRps,
    bool TargetLoadAchieved,
    bool GeneratorSaturated,
    string? AbortReason,
    IReadOnlyDictionary<string, int> ErrorCategories,
    LatencyHistogram Latency
);

