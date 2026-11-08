namespace ApiWrangler.Domain;

public sealed record ControlPlaneJobQueueItem(
    string JobId,
    RunnerJobDefinition Definition,
    RunnerJobOutcome Status,
    string? AssignedRunnerId,
    string? CurrentLeaseId,
    long CurrentFencingToken,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? HeartbeatLastReceivedAt,
    int AttemptCount,
    int MaxAttempts = 1,
    bool IsMutating = true,
    RunnerJobResult? Result = null,
    bool CancellationRequested = false
);

public enum ArtifactBackendType
{
    LocalFilesystem,
    SharedStorage,
    GitHubActions,
    S3Compatible
}

public sealed record ArtifactMetadata(
    string ArtifactId,
    string JobId,
    string Name,
    ArtifactBackendType Backend,
    string StoragePathOrUri,
    long SizeBytes,
    string ContentType,
    string Sha256Checksum,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null,
    bool IsRetained = false
);

public sealed record ArtifactPolicyConfig(
    long MaxSizeBytes = 50 * 1024 * 1024, // 50 MiB
    TimeSpan? RetentionPeriod = null,
    bool RetainFailuresOnly = false,
    bool IsRetained = false
);

public sealed record ControlPlaneHealthReport(
    bool IsHealthy,
    int ActiveRunners,
    int QueuedJobs,
    int ActiveLeases,
    long StorageBytesUsed,
    IReadOnlyList<string> Diagnostics
);
