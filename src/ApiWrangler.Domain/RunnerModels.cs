using System.Text.Json.Serialization;

namespace ApiWrangler.Domain;

public enum RunnerPlatform
{
    Windows,
    Linux,
    Container
}

public enum RunnerStatus
{
    Offline,
    Idle,
    Busy,
    Draining
}

public enum RunnerEnrollmentScope
{
    RunnerRegistration,
    JobSubmission,
    Admin
}

public enum RunnerJobOutcome
{
    Queued,
    Assigned,
    Running,
    Passed,
    Failed,
    Indeterminate,
    Cancelled
}

public sealed record RunnerDescriptor(
    string RunnerId,
    string Name,
    RunnerPlatform Platform,
    IReadOnlyList<string> Capabilities,
    RunnerStatus Status,
    DateTimeOffset RegisteredAt,
    DateTimeOffset LastHeartbeatAt,
    string? CurrentJobId = null
);

public sealed record RunnerEnrollmentToken(
    string TokenId,
    string Name,
    RunnerEnrollmentScope Scope,
    string HashedSecret,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null,
    bool IsRevoked = false,
    IReadOnlyList<string>? AllowedCapabilities = null
);

public sealed record EnrollmentResult(
    bool Success,
    string? PlaintextToken = null,
    string? TokenId = null,
    string? ErrorMessage = null
);

public sealed record RunnerJobDefinition(
    string JobId,
    string ProjectId,
    string RepositoryUrl,
    string ExactCommitSha,
    string ProjectSubdirectory = ".",
    string ExecutionType = "CollectionRun",
    IReadOnlyList<string>? SelectedEntities = null,
    string? EnvironmentName = null,
    string? InputSnapshotRef = null,
    int TimeoutSeconds = 300,
    string ArtifactPolicy = "FailuresOnly",
    IReadOnlyList<string>? RequiredCapabilities = null
);

public sealed record RunnerJobLease(
    string LeaseId,
    string JobId,
    string RunnerId,
    long FencingToken,
    DateTimeOffset AcquiredAt,
    DateTimeOffset ExpiresAt,
    TimeSpan HeartbeatInterval
);

public sealed record RunnerJobResult(
    string JobId,
    RunnerJobOutcome Outcome,
    int ExitCode,
    string Summary,
    IReadOnlyList<string> ArtifactPaths,
    string ExecutedCommitSha,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string RunnerId,
    string? IndeterminateReason = null
);

