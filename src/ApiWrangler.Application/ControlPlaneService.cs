using System.Collections.Concurrent;
using System.Security.Cryptography;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed class ControlPlaneQueue
{
    private readonly ConcurrentDictionary<string, ControlPlaneJobQueueItem> jobs = new();
    private long globalFencingCounter = 1000;

    public bool EnqueueJob(RunnerJobDefinition job, bool isMutating = true, int maxAttempts = 1)
    {
        var item = new ControlPlaneJobQueueItem(
            JobId: job.JobId,
            Definition: job,
            Status: RunnerJobOutcome.Queued,
            AssignedRunnerId: null,
            CurrentLeaseId: null,
            CurrentFencingToken: 0,
            EnqueuedAt: DateTimeOffset.UtcNow,
            LeaseExpiresAt: null,
            HeartbeatLastReceivedAt: null,
            AttemptCount: 0,
            MaxAttempts: Math.Max(1, maxAttempts),
            IsMutating: isMutating
        );

        return jobs.TryAdd(job.JobId, item);
    }

    public RunnerJobLease? AcquireJobLease(
        string runnerId,
        IReadOnlyList<string> runnerCapabilities,
        TimeSpan leaseDuration)
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in jobs)
        {
            var item = kvp.Value;
            if (item.Status != RunnerJobOutcome.Queued) continue;

            // Check capability match
            if (!ExactCommitJobExecutor.MatchCapabilities(runnerCapabilities, item.Definition.RequiredCapabilities))
            {
                continue;
            }

            var fencingToken = Interlocked.Increment(ref globalFencingCounter);
            var leaseId = $"lease_{Guid.NewGuid():N}";
            var expiresAt = now.Add(leaseDuration);

            var updated = item with
            {
                Status = RunnerJobOutcome.Assigned,
                AssignedRunnerId = runnerId,
                CurrentLeaseId = leaseId,
                CurrentFencingToken = fencingToken,
                LeaseExpiresAt = expiresAt,
                HeartbeatLastReceivedAt = now,
                AttemptCount = item.AttemptCount + 1
            };

            if (jobs.TryUpdate(item.JobId, updated, item))
            {
                return new RunnerJobLease(
                    LeaseId: leaseId,
                    JobId: item.JobId,
                    RunnerId: runnerId,
                    FencingToken: fencingToken,
                    AcquiredAt: now,
                    ExpiresAt: expiresAt,
                    HeartbeatInterval: TimeSpan.FromSeconds(Math.Max(1, leaseDuration.TotalSeconds / 3.0))
                );
            }
        }

        return null;
    }

    public bool HeartbeatLease(
        string leaseId,
        string runnerId,
        long fencingToken,
        TimeSpan extension,
        out bool cancellationRequested)
    {
        cancellationRequested = false;
        var now = DateTimeOffset.UtcNow;

        foreach (var kvp in jobs)
        {
            var item = kvp.Value;
            if (item.CurrentLeaseId == leaseId)
            {
                // Strict fencing check: Stale or superseded runner cannot extend lease
                if (item.CurrentFencingToken != fencingToken || item.AssignedRunnerId != runnerId)
                {
                    return false;
                }

                if (item.Status != RunnerJobOutcome.Assigned && item.Status != RunnerJobOutcome.Running)
                {
                    return false;
                }

                cancellationRequested = item.CancellationRequested;
                var updated = item with
                {
                    Status = RunnerJobOutcome.Running,
                    LeaseExpiresAt = now.Add(extension),
                    HeartbeatLastReceivedAt = now
                };

                return jobs.TryUpdate(item.JobId, updated, item);
            }
        }

        return false;
    }

    public bool CompleteJob(
        string leaseId,
        string runnerId,
        long fencingToken,
        RunnerJobResult result)
    {
        foreach (var kvp in jobs)
        {
            var item = kvp.Value;
            if (item.CurrentLeaseId == leaseId)
            {
                // Strict fencing check: Stale runner whose lease expired or was superseded cannot complete job!
                if (item.CurrentFencingToken != fencingToken || item.AssignedRunnerId != runnerId)
                {
                    return false;
                }

                var updated = item with
                {
                    Status = result.Outcome,
                    Result = result,
                    LeaseExpiresAt = null
                };

                return jobs.TryUpdate(item.JobId, updated, item);
            }
        }

        return false;
    }

    public bool RequestCancellation(string jobId)
    {
        if (jobs.TryGetValue(jobId, out var item))
        {
            var updated = item with { CancellationRequested = true };
            return jobs.TryUpdate(jobId, updated, item);
        }
        return false;
    }

    public int ReconcileExpiredLeases(DateTimeOffset? currentTime = null)
    {
        var now = currentTime ?? DateTimeOffset.UtcNow;
        int reconciledCount = 0;

        foreach (var kvp in jobs)
        {
            var item = kvp.Value;
            if ((item.Status == RunnerJobOutcome.Assigned || item.Status == RunnerJobOutcome.Running)
                && item.LeaseExpiresAt.HasValue
                && now > item.LeaseExpiresAt.Value)
            {
                // Lease has expired / worker was lost!
                if (item.IsMutating)
                {
                    // Specification Section 20.5:
                    // "A crashed job must not be silently retried if tests may have changed external state.
                    // Mark its outcome indeterminate and apply the configured retry policy."
                    var indeterminateResult = new RunnerJobResult(
                        JobId: item.JobId,
                        Outcome: RunnerJobOutcome.Indeterminate,
                        ExitCode: 3,
                        Summary: "Runner lease expired without completion. Job outcome is indeterminate because operations may have altered external state.",
                        ArtifactPaths: Array.Empty<string>(),
                        ExecutedCommitSha: item.Definition.ExactCommitSha,
                        StartedAt: item.HeartbeatLastReceivedAt ?? item.EnqueuedAt,
                        CompletedAt: now,
                        RunnerId: item.AssignedRunnerId ?? "unknown",
                        IndeterminateReason: "Lease expired after runner heartbeat timeout. Lost mutating job marked Indeterminate to prevent silent replay."
                    );

                    var updated = item with
                    {
                        Status = RunnerJobOutcome.Indeterminate,
                        Result = indeterminateResult,
                        LeaseExpiresAt = null
                    };

                    if (jobs.TryUpdate(item.JobId, updated, item))
                    {
                        reconciledCount++;
                    }
                }
                else
                {
                    // Non-mutating job can be safely retried if within attempt budget
                    if (item.AttemptCount < item.MaxAttempts)
                    {
                        var updated = item with
                        {
                            Status = RunnerJobOutcome.Queued,
                            AssignedRunnerId = null,
                            CurrentLeaseId = null,
                            LeaseExpiresAt = null
                        };

                        if (jobs.TryUpdate(item.JobId, updated, item))
                        {
                            reconciledCount++;
                        }
                    }
                    else
                    {
                        var updated = item with
                        {
                            Status = RunnerJobOutcome.Failed,
                            LeaseExpiresAt = null
                        };

                        if (jobs.TryUpdate(item.JobId, updated, item))
                        {
                            reconciledCount++;
                        }
                    }
                }
            }
        }

        return reconciledCount;
    }

    public ControlPlaneJobQueueItem? GetJob(string jobId)
    {
        jobs.TryGetValue(jobId, out var item);
        return item;
    }

    public IReadOnlyList<ControlPlaneJobQueueItem> GetAllJobs()
    {
        return jobs.Values.ToList();
    }
}

public sealed class ArtifactStorageService
{
    private readonly string rootDirectory;
    private readonly ConcurrentDictionary<string, ArtifactMetadata> artifacts = new();

    public ArtifactStorageService(string rootDirectory)
    {
        this.rootDirectory = rootDirectory;
        Directory.CreateDirectory(rootDirectory);
    }

    public async Task<ArtifactMetadata> StoreArtifactAsync(
        string jobId,
        string name,
        Stream contentStream,
        string contentType,
        ArtifactPolicyConfig? policy = null,
        CancellationToken cancellationToken = default)
    {
        var config = policy ?? new ArtifactPolicyConfig();
        var artifactId = $"art_{Guid.NewGuid():N}";
        var jobDir = Path.Combine(rootDirectory, jobId);
        Directory.CreateDirectory(jobDir);

        var destinationPath = Path.Combine(jobDir, $"{artifactId}_{Path.GetFileName(name)}");
        long totalBytes = 0;
        string checksumHex;

        using (var hashAlgorithm = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        await using (var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await contentStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                totalBytes += read;
                if (totalBytes > config.MaxSizeBytes)
                {
                    fileStream.Close();
                    File.Delete(destinationPath);
                    throw new InvalidOperationException($"Artifact size exceeded policy limit of {config.MaxSizeBytes} bytes.");
                }

                hashAlgorithm.AppendData(buffer, 0, read);
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            checksumHex = Convert.ToHexString(hashAlgorithm.GetHashAndReset()).ToLowerInvariant();
        }

        var now = DateTimeOffset.UtcNow;
        var expiresAt = config.RetentionPeriod.HasValue ? now.Add(config.RetentionPeriod.Value) : (DateTimeOffset?)null;

        var metadata = new ArtifactMetadata(
            ArtifactId: artifactId,
            JobId: jobId,
            Name: name,
            Backend: ArtifactBackendType.LocalFilesystem,
            StoragePathOrUri: destinationPath,
            SizeBytes: totalBytes,
            ContentType: contentType,
            Sha256Checksum: checksumHex,
            CreatedAt: now,
            ExpiresAt: expiresAt,
            IsRetained: config.IsRetained
        );

        artifacts[artifactId] = metadata;
        return metadata;
    }

    public ArtifactMetadata? GetArtifactMetadata(string artifactId)
    {
        artifacts.TryGetValue(artifactId, out var meta);
        return meta;
    }

    public Stream? OpenArtifactStream(string artifactId)
    {
        if (artifacts.TryGetValue(artifactId, out var meta) && File.Exists(meta.StoragePathOrUri))
        {
            return new FileStream(meta.StoragePathOrUri, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        return null;
    }

    public int ApplyRetentionSweep(DateTimeOffset? currentTime = null)
    {
        var now = currentTime ?? DateTimeOffset.UtcNow;
        int deleted = 0;

        foreach (var kvp in artifacts)
        {
            var meta = kvp.Value;
            if (meta.ExpiresAt.HasValue && now > meta.ExpiresAt.Value && !meta.IsRetained)
            {
                if (File.Exists(meta.StoragePathOrUri))
                {
                    try
                    {
                        File.Delete(meta.StoragePathOrUri);
                    }
                    catch (IOException) { }
                }

                artifacts.TryRemove(meta.ArtifactId, out _);
                deleted++;
            }
        }

        return deleted;
    }

    public ControlPlaneHealthReport GetHealthReport(int queuedJobs = 0, int activeLeases = 0, int activeRunners = 0)
    {
        long totalStorageBytes = artifacts.Values.Sum(a => a.SizeBytes);
        var diagnostics = new List<string>();

        bool isHealthy = Directory.Exists(rootDirectory);
        if (!isHealthy)
        {
            diagnostics.Add("Root storage directory is inaccessible.");
        }
        else
        {
            diagnostics.Add($"Storage root available: {rootDirectory}");
            diagnostics.Add($"Tracked artifacts: {artifacts.Count} ({totalStorageBytes} bytes)");
        }

        return new ControlPlaneHealthReport(
            IsHealthy: isHealthy,
            ActiveRunners: activeRunners,
            QueuedJobs: queuedJobs,
            ActiveLeases: activeLeases,
            StorageBytesUsed: totalStorageBytes,
            Diagnostics: diagnostics
        );
    }
}
