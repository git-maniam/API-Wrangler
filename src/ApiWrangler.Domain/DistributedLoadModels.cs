namespace ApiWrangler.Domain;

public sealed record DistributedWorkerAssignment(
    string WorkerId,
    int AssignedVirtualUsers,
    int AssignedTargetRps,
    int InputSliceStartIndex,
    int InputSliceCount,
    DateTimeOffset CoordinatedStartUtc
);

public sealed record DistributedLoadPlan(
    string PlanId,
    string TargetUrl,
    string SnapshotHash,
    int TotalVirtualUsers,
    int TotalTargetRps,
    TimeSpan Duration,
    IReadOnlyList<DistributedWorkerAssignment> WorkerAssignments,
    LoadThresholdPolicy? Thresholds = null
);

public sealed record DistributedWorkerReport(
    string WorkerId,
    string ReportedSnapshotHash,
    double AchievedRps,
    int TotalRequests,
    int SuccessfulRequests,
    int FailedRequests,
    LatencyHistogram Latency,
    IReadOnlyDictionary<string, int> ErrorCategories,
    bool WorkerSaturated,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc
);

public sealed record DistributedLoadSummary(
    string PlanId,
    string SnapshotHash,
    int ConfiguredWorkers,
    int ReportingWorkers,
    IReadOnlyList<string> MissingWorkerIds,
    int TotalRequests,
    int SuccessfulRequests,
    int FailedRequests,
    double AggregatedAchievedRps,
    double TargetRps,
    bool TargetAchieved,
    bool AnyWorkerSaturated,
    LatencyHistogram AggregatedLatency,
    IReadOnlyDictionary<string, int> AggregatedErrorCategories,
    LoadThresholdEvaluation? ThresholdEvaluation,
    string Status
);

