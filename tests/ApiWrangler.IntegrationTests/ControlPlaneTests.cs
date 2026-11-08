using System.Text;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class ControlPlaneTests
{
    [Fact]
    public void ControlPlane_AtLeastOnceDispatch_AndFencedOwnership()
    {
        var queue = new ControlPlaneQueue();
        var job = new RunnerJobDefinition(
            JobId: "job-fencing-1",
            ProjectId: "proj-1",
            RepositoryUrl: "https://github.com/org/repo.git",
            ExactCommitSha: "abcdef1234567890abcdef1234567890abcdef12",
            RequiredCapabilities: new[] { "windows" }
        );

        Assert.True(queue.EnqueueJob(job));

        // 1. Ineligible runner cannot acquire lease
        var badLease = queue.AcquireJobLease("linux-worker", new[] { "linux" }, TimeSpan.FromMinutes(5));
        Assert.Null(badLease);

        // 2. Eligible runner acquires lease with fencing token
        var lease = queue.AcquireJobLease("win-worker", new[] { "windows" }, TimeSpan.FromMinutes(5));
        Assert.NotNull(lease);
        Assert.Equal("job-fencing-1", lease.JobId);
        Assert.True(lease.FencingToken > 0);

        // 3. Heartbeat extends lease and returns cancellation flag
        var heartbeatOk = queue.HeartbeatLease(lease.LeaseId, lease.RunnerId, lease.FencingToken, TimeSpan.FromMinutes(10), out var cancelRequested);
        Assert.True(heartbeatOk);
        Assert.False(cancelRequested);

        // 4. Stale/wrong fencing token heartbeat is rejected
        var staleHeartbeat = queue.HeartbeatLease(lease.LeaseId, lease.RunnerId, fencingToken: lease.FencingToken - 1, TimeSpan.FromMinutes(5), out _);
        Assert.False(staleHeartbeat);

        // 5. Stale completion is rejected
        var dummyResult = new RunnerJobResult(
            JobId: job.JobId,
            Outcome: RunnerJobOutcome.Passed,
            ExitCode: 0,
            Summary: "Done",
            ArtifactPaths: Array.Empty<string>(),
            ExecutedCommitSha: job.ExactCommitSha,
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow,
            RunnerId: lease.RunnerId
        );
        var staleComplete = queue.CompleteJob(lease.LeaseId, lease.RunnerId, fencingToken: lease.FencingToken - 1, dummyResult);
        Assert.False(staleComplete);

        // 6. Valid completion with correct fencing token succeeds
        var validComplete = queue.CompleteJob(lease.LeaseId, lease.RunnerId, lease.FencingToken, dummyResult);
        Assert.True(validComplete);

        var finalJob = queue.GetJob(job.JobId);
        Assert.NotNull(finalJob);
        Assert.Equal(RunnerJobOutcome.Passed, finalJob.Status);
    }

    [Fact]
    public void ControlPlane_LostMutatingJob_BecomesIndeterminate_NotSilentlyRetried()
    {
        var queue = new ControlPlaneQueue();
        var mutatingJob = new RunnerJobDefinition(
            JobId: "job-mutating-crash",
            ProjectId: "proj-1",
            RepositoryUrl: "https://github.com/org/repo.git",
            ExactCommitSha: "abcdef1234567890abcdef1234567890abcdef12"
        );

        // Enqueue as mutating job
        Assert.True(queue.EnqueueJob(mutatingJob, isMutating: true));

        var lease = queue.AcquireJobLease("worker-1", Array.Empty<string>(), TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);

        // Simulate crash by advancing time past lease expiration
        var simulatedNow = DateTimeOffset.UtcNow.AddMinutes(5);
        int reconciled = queue.ReconcileExpiredLeases(simulatedNow);
        Assert.Equal(1, reconciled);

        var jobRecord = queue.GetJob(mutatingJob.JobId);
        Assert.NotNull(jobRecord);
        // Acceptance criterion: Lost potentially mutating jobs become indeterminate and are not silently retried
        Assert.Equal(RunnerJobOutcome.Indeterminate, jobRecord.Status);
        Assert.NotNull(jobRecord.Result);
        Assert.NotNull(jobRecord.Result.IndeterminateReason);
        Assert.Contains("Lost mutating job marked Indeterminate", jobRecord.Result.IndeterminateReason);
    }

    [Fact]
    public void ControlPlane_LostNonMutatingJob_RetriesWithinBudget()
    {
        var queue = new ControlPlaneQueue();
        var readOnlyJob = new RunnerJobDefinition(
            JobId: "job-readonly-retry",
            ProjectId: "proj-1",
            RepositoryUrl: "https://github.com/org/repo.git",
            ExactCommitSha: "abcdef1234567890abcdef1234567890abcdef12"
        );

        // Non-mutating job with max 2 attempts
        Assert.True(queue.EnqueueJob(readOnlyJob, isMutating: false, maxAttempts: 2));

        var lease = queue.AcquireJobLease("worker-1", Array.Empty<string>(), TimeSpan.FromSeconds(30));
        Assert.NotNull(lease);

        // Lease expires
        var simulatedNow = DateTimeOffset.UtcNow.AddMinutes(5);
        int reconciled = queue.ReconcileExpiredLeases(simulatedNow);
        Assert.Equal(1, reconciled);

        // Safe retry re-enqueues job for attempt 2
        var retriedJob = queue.GetJob(readOnlyJob.JobId);
        Assert.NotNull(retriedJob);
        Assert.Equal(RunnerJobOutcome.Queued, retriedJob.Status);
        Assert.Equal(1, retriedJob.AttemptCount);
    }

    [Fact]
    public void ControlPlane_Cancellation_PropagatesToRunnerDuringHeartbeat()
    {
        var queue = new ControlPlaneQueue();
        var job = new RunnerJobDefinition(
            JobId: "job-cancel-test",
            ProjectId: "proj-1",
            RepositoryUrl: "https://github.com/org/repo.git",
            ExactCommitSha: "abcdef1234567890abcdef1234567890abcdef12"
        );

        queue.EnqueueJob(job);
        var lease = queue.AcquireJobLease("worker-1", Array.Empty<string>(), TimeSpan.FromMinutes(2));
        Assert.NotNull(lease);

        // User or scheduler requests cancellation
        Assert.True(queue.RequestCancellation(job.JobId));

        // Runner heartbeats and receives cancellation signal
        var heartbeatOk = queue.HeartbeatLease(lease.LeaseId, lease.RunnerId, lease.FencingToken, TimeSpan.FromMinutes(2), out var cancelRequested);
        Assert.True(heartbeatOk);
        Assert.True(cancelRequested);
    }

    [Fact]
    public async Task ArtifactStorage_EnforcesMaxSizeBytes_AndCalculatesChecksum()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "awr-artifacts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ArtifactStorageService(tempDir);

            // 1. Normal artifact storage
            var contentString = "{\"report\": true, \"requests\": 500}";
            var contentBytes = Encoding.UTF8.GetBytes(contentString);
            using var stream = new MemoryStream(contentBytes);

            var meta = await service.StoreArtifactAsync("job-100", "report.json", stream, "application/json");
            Assert.NotNull(meta);
            Assert.Equal(contentBytes.Length, meta.SizeBytes);
            Assert.NotEmpty(meta.Sha256Checksum);

            // Retrieve content
            using var readStream = service.OpenArtifactStream(meta.ArtifactId);
            Assert.NotNull(readStream);
            using var reader = new StreamReader(readStream);
            var retrieved = await reader.ReadToEndAsync();
            Assert.Equal(contentString, retrieved);

            // 2. Policy size limit exceeded
            var largeStream = new MemoryStream(new byte[1024]);
            var strictPolicy = new ArtifactPolicyConfig(MaxSizeBytes: 500); // 500 bytes limit
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.StoreArtifactAsync("job-100", "too-large.bin", largeStream, "application/octet-stream", strictPolicy));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ArtifactStorage_RetentionSweep_CleansExpiredOperationalArtifacts()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "awr-artifacts-sweep-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ArtifactStorageService(tempDir);

            // Expired artifact
            var expiredPolicy = new ArtifactPolicyConfig(RetentionPeriod: TimeSpan.FromSeconds(-10));
            using var stream1 = new MemoryStream(new byte[] { 1, 2, 3 });
            var expiredMeta = await service.StoreArtifactAsync("job-101", "temp.log", stream1, "text/plain", expiredPolicy);

            // Retained artifact (even with passed expiration, explicit retention protects it)
            var retainedPolicy = new ArtifactPolicyConfig(RetentionPeriod: TimeSpan.FromSeconds(-10), IsRetained: true);
            using var stream2 = new MemoryStream(new byte[] { 4, 5, 6 });
            var retainedMeta = await service.StoreArtifactAsync("job-101", "summary.md", stream2, "text/markdown", retainedPolicy);

            Assert.True(File.Exists(expiredMeta.StoragePathOrUri));
            Assert.True(File.Exists(retainedMeta.StoragePathOrUri));

            // Sweep
            int swept = service.ApplyRetentionSweep();
            Assert.Equal(1, swept);

            Assert.False(File.Exists(expiredMeta.StoragePathOrUri));
            Assert.True(File.Exists(retainedMeta.StoragePathOrUri));

            // Health report
            var health = service.GetHealthReport(queuedJobs: 2, activeLeases: 1, activeRunners: 3);
            Assert.True(health.IsHealthy);
            Assert.Equal(2, health.QueuedJobs);
            Assert.Equal(1, health.ActiveLeases);
            Assert.Equal(3, health.ActiveRunners);
            Assert.True(health.StorageBytesUsed > 0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
