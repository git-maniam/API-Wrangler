using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed class DistributedLoadCoordinator
{
    public DistributedLoadPlan PlanDistribution(
        string targetUrl,
        string snapshotHash,
        int totalVirtualUsers,
        int totalTargetRps,
        TimeSpan duration,
        IReadOnlyList<string> workerIds,
        int totalDatasetRows = 0,
        LoadThresholdPolicy? thresholds = null)
    {
        if (workerIds is null || workerIds.Count == 0)
        {
            throw new ArgumentException("At least one worker must be specified for distributed load planning.", nameof(workerIds));
        }

        int workerCount = workerIds.Count;
        int baseVu = totalVirtualUsers / workerCount;
        int vuRemainder = totalVirtualUsers % workerCount;

        int baseRps = totalTargetRps / workerCount;
        int rpsRemainder = totalTargetRps % workerCount;

        int rowsPerWorker = totalDatasetRows > 0 ? totalDatasetRows / workerCount : 0;
        int rowRemainder = totalDatasetRows > 0 ? totalDatasetRows % workerCount : 0;

        var coordinatedStart = DateTimeOffset.UtcNow.AddSeconds(2);
        var assignments = new List<DistributedWorkerAssignment>();
        int currentRowIndex = 0;

        for (int i = 0; i < workerCount; i++)
        {
            int assignedVu = baseVu + (i < vuRemainder ? 1 : 0);
            int assignedRps = baseRps + (i < rpsRemainder ? 1 : 0);
            int assignedRows = rowsPerWorker + (i < rowRemainder ? 1 : 0);

            assignments.Add(new DistributedWorkerAssignment(
                WorkerId: workerIds[i],
                AssignedVirtualUsers: Math.Max(1, assignedVu),
                AssignedTargetRps: Math.Max(1, assignedRps),
                InputSliceStartIndex: currentRowIndex,
                InputSliceCount: assignedRows,
                CoordinatedStartUtc: coordinatedStart
            ));

            currentRowIndex += assignedRows;
        }

        return new DistributedLoadPlan(
            PlanId: $"dist_plan_{Guid.NewGuid():N}",
            TargetUrl: targetUrl,
            SnapshotHash: snapshotHash,
            TotalVirtualUsers: totalVirtualUsers,
            TotalTargetRps: totalTargetRps,
            Duration: duration,
            WorkerAssignments: assignments,
            Thresholds: thresholds
        );
    }

    public DistributedLoadSummary AggregateResults(
        DistributedLoadPlan plan,
        IReadOnlyList<DistributedWorkerReport> workerReports)
    {
        // 1. Strict snapshot validation: Exit criteria Section 19.4 & 32:
        // "All workers report the same snapshot and compatible histograms; runs do not silently continue against different snapshots."
        var mismatchedWorkers = workerReports
            .Where(r => !string.Equals(r.ReportedSnapshotHash, plan.SnapshotHash, StringComparison.OrdinalIgnoreCase))
            .Select(r => r.WorkerId)
            .ToList();

        if (mismatchedWorkers.Count > 0)
        {
            return new DistributedLoadSummary(
                PlanId: plan.PlanId,
                SnapshotHash: plan.SnapshotHash,
                ConfiguredWorkers: plan.WorkerAssignments.Count,
                ReportingWorkers: workerReports.Count,
                MissingWorkerIds: Array.Empty<string>(),
                TotalRequests: 0,
                SuccessfulRequests: 0,
                FailedRequests: 0,
                AggregatedAchievedRps: 0,
                TargetRps: plan.TotalTargetRps,
                TargetAchieved: false,
                AnyWorkerSaturated: false,
                AggregatedLatency: new LatencyHistogram(Array.Empty<long>(), 0, 0, 0, 0, 0, 0, 0),
                AggregatedErrorCategories: new Dictionary<string, int>
                {
                    ["SnapshotMismatch"] = mismatchedWorkers.Count
                },
                ThresholdEvaluation: new LoadThresholdEvaluation(false, new[]
                {
                    $"Run aborted: Workers [{string.Join(", ", mismatchedWorkers)}] reported snapshots different from planned snapshot {plan.SnapshotHash}."
                }),
                Status: "SnapshotMismatchRejected"
            );
        }

        // 2. Identify missing workers
        var reportingWorkerIds = new HashSet<string>(workerReports.Select(r => r.WorkerId), StringComparer.OrdinalIgnoreCase);
        var missingWorkerIds = plan.WorkerAssignments
            .Where(a => !reportingWorkerIds.Contains(a.WorkerId))
            .Select(a => a.WorkerId)
            .ToList();

        bool hasWorkerLoss = missingWorkerIds.Count > 0;
        string status = hasWorkerLoss ? "PartialWorkerLoss" : "Completed";

        // 3. Truthful aggregation across reporting workers
        int totalRequests = workerReports.Sum(r => r.TotalRequests);
        int successfulRequests = workerReports.Sum(r => r.SuccessfulRequests);
        int failedRequests = workerReports.Sum(r => r.FailedRequests);
        double achievedRps = workerReports.Sum(r => r.AchievedRps);
        bool anySaturated = workerReports.Any(r => r.WorkerSaturated);

        // Merge histograms pooling all measurements to compute exact percentiles
        var histograms = workerReports.Select(r => r.Latency).ToList();
        var mergedLatency = LoadExecutionEngine.MergeHistograms(histograms);

        // Pool error categories
        var pooledErrors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var report in workerReports)
        {
            foreach (var kvp in report.ErrorCategories)
            {
                pooledErrors[kvp.Key] = pooledErrors.GetValueOrDefault(kvp.Key) + kvp.Value;
            }
        }

        bool targetAchieved = achievedRps >= plan.TotalTargetRps * 0.95 && !hasWorkerLoss;

        // Construct synthetic report for threshold evaluation
        var syntheticReport = new LoadTestReport(
            TargetUrl: plan.TargetUrl,
            ModelType: LoadModelType.ClosedLoopVirtualUsers,
            ProfileType: LoadProfileType.FixedUsers,
            StartedAt: workerReports.Count > 0 ? workerReports.Min(r => r.StartedAtUtc) : DateTimeOffset.UtcNow,
            CompletedAt: workerReports.Count > 0 ? workerReports.Max(r => r.CompletedAtUtc) : DateTimeOffset.UtcNow,
            TotalDuration: plan.Duration,
            TotalRequests: totalRequests,
            SuccessfulRequests: successfulRequests,
            FailedRequests: failedRequests,
            AchievedRps: achievedRps,
            TargetRps: plan.TotalTargetRps,
            TargetLoadAchieved: targetAchieved,
            GeneratorSaturated: anySaturated,
            AbortReason: hasWorkerLoss ? $"Warning: {missingWorkerIds.Count} worker(s) failed to report: [{string.Join(", ", missingWorkerIds)}]." : null,
            ErrorCategories: pooledErrors,
            Latency: mergedLatency
        );

        var thresholdEvaluation = LoadExecutionEngine.EvaluateThresholds(plan.Thresholds, syntheticReport);

        return new DistributedLoadSummary(
            PlanId: plan.PlanId,
            SnapshotHash: plan.SnapshotHash,
            ConfiguredWorkers: plan.WorkerAssignments.Count,
            ReportingWorkers: workerReports.Count,
            MissingWorkerIds: missingWorkerIds,
            TotalRequests: totalRequests,
            SuccessfulRequests: successfulRequests,
            FailedRequests: failedRequests,
            AggregatedAchievedRps: achievedRps,
            TargetRps: plan.TotalTargetRps,
            TargetAchieved: targetAchieved,
            AnyWorkerSaturated: anySaturated,
            AggregatedLatency: mergedLatency,
            AggregatedErrorCategories: pooledErrors,
            ThresholdEvaluation: thresholdEvaluation,
            Status: status
        );
    }
}

