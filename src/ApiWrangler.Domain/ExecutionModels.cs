namespace ApiWrangler.Domain;

public enum RunStatus
{
    NotRun,
    Passed,
    Failed,
    Error,
    Skipped,
    Cancelled,
    TimedOut
}

public enum TeardownPolicy
{
    Always,
    OnFailure,
    OnSuccess
}

public sealed record FlowControlDirective
{
    public Guid? NextRequestId { get; init; }
    public bool SkipNext { get; init; }
    public bool StopIteration { get; init; }
    public bool StopCollection { get; init; }
    public int MaxLoopBudget { get; init; } = 50;
    public int PollIntervalMs { get; init; } = 500;
}

public sealed record CollectionExecutionOptions
{
    public int Iterations { get; init; } = 1;
    public int Concurrency { get; init; } = 1;
    public int DelayBetweenRequestsMs { get; init; } = 0;
    public bool StopOnFailure { get; init; } = false;
    public bool IsolateCookiesPerIteration { get; init; } = true;
    public TeardownPolicy TeardownPolicy { get; init; } = TeardownPolicy.Always;
    public IReadOnlyList<Guid>? SelectedRequestIds { get; init; }
    public IReadOnlyList<string>? SelectedTags { get; init; }
}

public sealed record RequestExecutionRecord(
    Guid RequestId,
    string RequestName,
    int Iteration,
    int Attempt,
    ExecutionOutcome Outcome,
    int? StatusCode,
    TimeSpan Duration,
    string? Error = null,
    string? ResponsePreview = null,
    string? BodyPath = null);

public sealed record IterationExecutionRecord(
    int IterationIndex,
    RunStatus Status,
    TimeSpan Duration,
    IReadOnlyList<RequestExecutionRecord> Requests,
    IReadOnlyDictionary<string, string> FinalVariables,
    string? Error = null);

public sealed record CollectionExecutionReport(
    Guid RunId,
    Guid CollectionId,
    string CollectionName,
    RunStatus Status,
    DateTimeOffset StartTimeUtc,
    DateTimeOffset EndTimeUtc,
    TimeSpan Duration,
    IReadOnlyList<IterationExecutionRecord> Iterations,
    IReadOnlyList<RequestExecutionRecord> SetupResults,
    IReadOnlyList<RequestExecutionRecord> TeardownResults,
    int TotalRequestsRun,
    int PassedRequests,
    int FailedRequests,
    string? Error = null);
