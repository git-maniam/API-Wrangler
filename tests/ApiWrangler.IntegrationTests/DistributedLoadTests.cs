using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class DistributedLoadTests
{
    [Fact]
    public void DistributedLoad_PlansInputSlicesAndVirtualUsers_Evenly()
    {
        var coordinator = new DistributedLoadCoordinator();
        var workers = new[] { "worker-us-east-1", "worker-us-west-2", "worker-eu-west-1" };

        var plan = coordinator.PlanDistribution(
            targetUrl: "https://api.example.com/v1/orders",
            snapshotHash: "snapshot-sha-abcdef12345",
            totalVirtualUsers: 10,
            totalTargetRps: 100,
            duration: TimeSpan.FromSeconds(30),
            workerIds: workers,
            totalDatasetRows: 50
        );

        Assert.Equal(3, plan.WorkerAssignments.Count);

        // Virtual users: 4 + 3 + 3 = 10
        Assert.Equal(4, plan.WorkerAssignments[0].AssignedVirtualUsers);
        Assert.Equal(3, plan.WorkerAssignments[1].AssignedVirtualUsers);
        Assert.Equal(3, plan.WorkerAssignments[2].AssignedVirtualUsers);
        Assert.Equal(10, plan.WorkerAssignments.Sum(a => a.AssignedVirtualUsers));

        // Target RPS: 34 + 33 + 33 = 100
        Assert.Equal(34, plan.WorkerAssignments[0].AssignedTargetRps);
        Assert.Equal(33, plan.WorkerAssignments[1].AssignedTargetRps);
        Assert.Equal(33, plan.WorkerAssignments[2].AssignedTargetRps);
        Assert.Equal(100, plan.WorkerAssignments.Sum(a => a.AssignedTargetRps));

        // Slices: contiguous partition of 50 rows
        Assert.Equal(0, plan.WorkerAssignments[0].InputSliceStartIndex);
        Assert.Equal(17, plan.WorkerAssignments[0].InputSliceCount);

        Assert.Equal(17, plan.WorkerAssignments[1].InputSliceStartIndex);
        Assert.Equal(17, plan.WorkerAssignments[1].InputSliceCount);

        Assert.Equal(34, plan.WorkerAssignments[2].InputSliceStartIndex);
        Assert.Equal(16, plan.WorkerAssignments[2].InputSliceCount);

        Assert.Equal(50, plan.WorkerAssignments.Sum(a => a.InputSliceCount));

        // Coordinated start in future
        Assert.True(plan.WorkerAssignments[0].CoordinatedStartUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void DistributedLoad_RejectsRunsWithMismatchedSnapshots()
    {
        var coordinator = new DistributedLoadCoordinator();
        var plan = coordinator.PlanDistribution(
            targetUrl: "https://api.example.com",
            snapshotHash: "approved-sha-1111",
            totalVirtualUsers: 4,
            totalTargetRps: 40,
            duration: TimeSpan.FromSeconds(10),
            workerIds: new[] { "w1", "w2" }
        );

        var report1 = new DistributedWorkerReport(
            WorkerId: "w1",
            ReportedSnapshotHash: "approved-sha-1111",
            AchievedRps: 20.0,
            TotalRequests: 200,
            SuccessfulRequests: 200,
            FailedRequests: 0,
            Latency: LoadExecutionEngine.CalculateHistogram(new[] { TimeSpan.FromMilliseconds(10) }),
            ErrorCategories: new Dictionary<string, int>(),
            WorkerSaturated: false,
            StartedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-10),
            CompletedAtUtc: DateTimeOffset.UtcNow
        );

        // Mismatched snapshot from w2
        var report2 = new DistributedWorkerReport(
            WorkerId: "w2",
            ReportedSnapshotHash: "dirty-unapproved-sha-2222",
            AchievedRps: 20.0,
            TotalRequests: 200,
            SuccessfulRequests: 200,
            FailedRequests: 0,
            Latency: LoadExecutionEngine.CalculateHistogram(new[] { TimeSpan.FromMilliseconds(10) }),
            ErrorCategories: new Dictionary<string, int>(),
            WorkerSaturated: false,
            StartedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-10),
            CompletedAtUtc: DateTimeOffset.UtcNow
        );

        var summary = coordinator.AggregateResults(plan, new[] { report1, report2 });

        Assert.Equal("SnapshotMismatchRejected", summary.Status);
        Assert.False(summary.TargetAchieved);
        Assert.NotNull(summary.ThresholdEvaluation);
        Assert.False(summary.ThresholdEvaluation.Passed);
        Assert.Contains(summary.ThresholdEvaluation.Violations, v => v.Contains("w2") && v.Contains("reported snapshots different"));
    }

    [Fact]
    public void DistributedLoad_TruthfulAggregation_MergesCompatibleHistogramsWithoutAveraging()
    {
        var coordinator = new DistributedLoadCoordinator();
        var plan = coordinator.PlanDistribution(
            targetUrl: "https://api.example.com",
            snapshotHash: "snap-abc",
            totalVirtualUsers: 2,
            totalTargetRps: 20,
            duration: TimeSpan.FromSeconds(5),
            workerIds: new[] { "w1", "w2" }
        );

        // Worker 1: 50 requests at 20ms
        var h1 = LoadExecutionEngine.CalculateHistogram(Enumerable.Repeat(TimeSpan.FromMilliseconds(20), 50).ToList());
        var report1 = new DistributedWorkerReport(
            WorkerId: "w1",
            ReportedSnapshotHash: "snap-abc",
            AchievedRps: 10.0,
            TotalRequests: 50,
            SuccessfulRequests: 50,
            FailedRequests: 0,
            Latency: h1,
            ErrorCategories: new Dictionary<string, int>(),
            WorkerSaturated: false,
            StartedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAtUtc: DateTimeOffset.UtcNow
        );

        // Worker 2: 50 requests at 80ms
        var h2 = LoadExecutionEngine.CalculateHistogram(Enumerable.Repeat(TimeSpan.FromMilliseconds(80), 50).ToList());
        var report2 = new DistributedWorkerReport(
            WorkerId: "w2",
            ReportedSnapshotHash: "snap-abc",
            AchievedRps: 10.0,
            TotalRequests: 50,
            SuccessfulRequests: 50,
            FailedRequests: 0,
            Latency: h2,
            ErrorCategories: new Dictionary<string, int>(),
            WorkerSaturated: false,
            StartedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAtUtc: DateTimeOffset.UtcNow
        );

        var summary = coordinator.AggregateResults(plan, new[] { report1, report2 });

        Assert.Equal("Completed", summary.Status);
        Assert.Equal(100, summary.TotalRequests);
        Assert.Equal(100, summary.SuccessfulRequests);
        Assert.Equal(0, summary.FailedRequests);
        Assert.Equal(20.0, summary.AggregatedAchievedRps);
        Assert.True(summary.TargetAchieved);

        // Merged latency percentiles across combined 100 items (50 at 20ms, 50 at 80ms)
        Assert.Equal(20, summary.AggregatedLatency.MinMs);
        Assert.Equal(80, summary.AggregatedLatency.MaxMs);
        Assert.Equal(50, summary.AggregatedLatency.MeanMs);
        Assert.Equal(20, summary.AggregatedLatency.P50Ms);
        Assert.Equal(80, summary.AggregatedLatency.P90Ms);
        Assert.Equal(80, summary.AggregatedLatency.P95Ms);
        Assert.Equal(80, summary.AggregatedLatency.P99Ms);
    }

    [Fact]
    public void DistributedLoad_WorkerLoss_RemainsVisibleAndTruthfullyReported()
    {
        var coordinator = new DistributedLoadCoordinator();
        var plan = coordinator.PlanDistribution(
            targetUrl: "https://api.example.com",
            snapshotHash: "snap-xyz",
            totalVirtualUsers: 3,
            totalTargetRps: 30,
            duration: TimeSpan.FromSeconds(5),
            workerIds: new[] { "worker-a", "worker-b", "worker-c" }
        );

        // Only worker-a and worker-b report; worker-c was lost
        var r1 = new DistributedWorkerReport(
            WorkerId: "worker-a",
            ReportedSnapshotHash: "snap-xyz",
            AchievedRps: 10.0,
            TotalRequests: 50,
            SuccessfulRequests: 50,
            FailedRequests: 0,
            Latency: LoadExecutionEngine.CalculateHistogram(new[] { TimeSpan.FromMilliseconds(15) }),
            ErrorCategories: new Dictionary<string, int>(),
            WorkerSaturated: false,
            StartedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAtUtc: DateTimeOffset.UtcNow
        );

        var r2 = new DistributedWorkerReport(
            WorkerId: "worker-b",
            ReportedSnapshotHash: "snap-xyz",
            AchievedRps: 10.0,
            TotalRequests: 50,
            SuccessfulRequests: 48,
            FailedRequests: 2,
            Latency: LoadExecutionEngine.CalculateHistogram(new[] { TimeSpan.FromMilliseconds(25) }),
            ErrorCategories: new Dictionary<string, int> { ["HTTP 503"] = 2 },
            WorkerSaturated: false,
            StartedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAtUtc: DateTimeOffset.UtcNow
        );

        var summary = coordinator.AggregateResults(plan, new[] { r1, r2 });

        // Worker loss is visible
        Assert.Equal("PartialWorkerLoss", summary.Status);
        Assert.Equal(3, summary.ConfiguredWorkers);
        Assert.Equal(2, summary.ReportingWorkers);
        Assert.Single(summary.MissingWorkerIds);
        Assert.Contains("worker-c", summary.MissingWorkerIds);

        // Achieved RPS does not fabricate missing worker load
        Assert.Equal(20.0, summary.AggregatedAchievedRps);
        Assert.False(summary.TargetAchieved); // 20 < 30 and worker loss
        Assert.Equal(100, summary.TotalRequests);
        Assert.Equal(2, summary.FailedRequests);
        Assert.Equal(2, summary.AggregatedErrorCategories["HTTP 503"]);
    }

    [Fact]
    public void DistributedLoad_EvaluatesThresholdsAcrossAggregatedWorkers()
    {
        var coordinator = new DistributedLoadCoordinator();
        var policy = new LoadThresholdPolicy(
            MaxP95LatencyMs: 50.0,
            MaxErrorRatePercent: 1.0,
            MinThroughputRps: 50.0
        );

        var plan = coordinator.PlanDistribution(
            targetUrl: "https://api.example.com",
            snapshotHash: "snap-thresh",
            totalVirtualUsers: 2,
            totalTargetRps: 60,
            duration: TimeSpan.FromSeconds(10),
            workerIds: new[] { "w1", "w2" },
            thresholds: policy
        );

        // w1 reports p95 = 120ms (exceeds 50ms)
        var h1 = LoadExecutionEngine.CalculateHistogram(Enumerable.Repeat(TimeSpan.FromMilliseconds(120), 100).ToList());
        var r1 = new DistributedWorkerReport(
            WorkerId: "w1",
            ReportedSnapshotHash: "snap-thresh",
            AchievedRps: 20.0,
            TotalRequests: 100,
            SuccessfulRequests: 90,
            FailedRequests: 10, // 10% error rate (exceeds 1%)
            Latency: h1,
            ErrorCategories: new Dictionary<string, int> { ["HTTP 500"] = 10 },
            WorkerSaturated: false,
            StartedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-10),
            CompletedAtUtc: DateTimeOffset.UtcNow
        );

        var r2 = new DistributedWorkerReport(
            WorkerId: "w2",
            ReportedSnapshotHash: "snap-thresh",
            AchievedRps: 20.0,
            TotalRequests: 100,
            SuccessfulRequests: 100,
            FailedRequests: 0,
            Latency: h1,
            ErrorCategories: new Dictionary<string, int>(),
            WorkerSaturated: false,
            StartedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-10),
            CompletedAtUtc: DateTimeOffset.UtcNow
        );

        var summary = coordinator.AggregateResults(plan, new[] { r1, r2 });

        Assert.NotNull(summary.ThresholdEvaluation);
        Assert.False(summary.ThresholdEvaluation.Passed);
        Assert.Contains(summary.ThresholdEvaluation.Violations, v => v.Contains("p95 latency threshold exceeded"));
        Assert.Contains(summary.ThresholdEvaluation.Violations, v => v.Contains("Error rate threshold exceeded"));
        Assert.Contains(summary.ThresholdEvaluation.Violations, v => v.Contains("Minimum throughput threshold failed"));
    }
}

