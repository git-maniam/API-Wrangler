using System.Diagnostics;
using System.Text.Json.Nodes;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed class CollectionRunner(HttpRequestService httpService)
{
    public async Task<CollectionExecutionReport> ExecuteCollectionAsync(
        ProjectView project,
        Guid collectionId,
        CollectionExecutionOptions? options = null,
        VariableContext? baseContext = null,
        SnapshotInfo? snapshot = null,
        CancellationToken cancellationToken = default)
    {
        var opts = options ?? new CollectionExecutionOptions();
        var startTime = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();

        var collectionAsset = project.Assets.FirstOrDefault(a => a.Kind == AssetKind.Collection &&
            string.Equals(a.Id, collectionId.ToString(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Collection '{collectionId}' was not found in project.", nameof(collectionId));

        var collectionName = collectionAsset.Name;
        var runId = Guid.NewGuid();

        // 1. Prepare Base Variable Context
        var context = (baseContext ?? new VariableContext()).Clone();
        if (collectionAsset.Content["variables"] is JsonObject collVars)
        {
            foreach (var kvp in collVars)
            {
                if (kvp.Value is JsonValue val)
                {
                    context.Collection[kvp.Key] = val.ToString();
                }
            }
        }

        // 2. Resolve Ordered Requests
        var requestIds = new List<Guid>();
        if (collectionAsset.Content["requestIds"] is JsonArray reqArr)
        {
            foreach (var node in reqArr)
            {
                if (node is JsonValue val && Guid.TryParse(val.GetValue<string>(), out var id))
                {
                    requestIds.Add(id);
                }
            }
        }

        if (opts.SelectedRequestIds is { Count: > 0 })
        {
            var selectedSet = new HashSet<Guid>(opts.SelectedRequestIds);
            requestIds = requestIds.Where(id => selectedSet.Contains(id)).ToList();
        }

        // Resolve setup and teardown request IDs
        var setupRequestIds = ExtractGuidList(collectionAsset.Content["setupRequestIds"]);
        var teardownRequestIds = ExtractGuidList(collectionAsset.Content["teardownRequestIds"]);

        var setupResults = new List<RequestExecutionRecord>();
        var teardownResults = new List<RequestExecutionRecord>();
        var iterationRecords = new List<IterationExecutionRecord>();
        bool collectionFailed = false;
        string? failureError = null;

        try
        {
            // 3. Collection Setup
            foreach (var setupId in setupRequestIds)
            {
                var setupAsset = FindRequestAsset(project, setupId);
                if (setupAsset is null) continue;

                var reqDef = RequestAssetSerializer.FromJsonObject(setupAsset.Content);
                var reqWatch = Stopwatch.StartNew();
                var res = await httpService.SendAsync(reqDef, variableContext: context, cancellationToken: cancellationToken).ConfigureAwait(false);
                reqWatch.Stop();

                var assertions = AssertionEngine.EvaluateAll(reqDef.Assertions, res);
                bool assertionFailed = assertions.Any(a => !a.Passed);

                ExecutionOutcome recordOutcome;
                if (res.Outcome != ExecutionOutcome.Passed)
                {
                    recordOutcome = res.Outcome;
                }
                else if (assertionFailed)
                {
                    recordOutcome = ExecutionOutcome.Failed;
                }
                else if (res.StatusCode.HasValue && res.StatusCode.Value >= 400 && reqDef.Assertions.Count == 0)
                {
                    recordOutcome = ExecutionOutcome.Failed;
                }
                else
                {
                    recordOutcome = ExecutionOutcome.Passed;
                }

                bool isFail = recordOutcome != ExecutionOutcome.Passed;
                var failMsg = assertionFailed
                    ? string.Join("; ", assertions.Where(a => !a.Passed).Select(a => $"{a.Name}: {a.Message}"))
                    : res.Error ?? (isFail ? $"HTTP {res.StatusCode}" : null);

                var record = new RequestExecutionRecord(
                    setupId,
                    reqDef.Name,
                    0,
                    1,
                    recordOutcome,
                    res.StatusCode,
                    reqWatch.Elapsed,
                    failMsg,
                    res.Preview,
                    res.BodyPath,
                    assertions);
                setupResults.Add(record);

                if (isFail)
                {
                    collectionFailed = true;
                    failureError = $"Collection setup request '{reqDef.Name}' failed: {failMsg}";
                    if (opts.StopOnFailure) break;
                }
            }

            // 4. Run Iterations
            if (!collectionFailed || !opts.StopOnFailure)
            {
                var semaphore = new SemaphoreSlim(Math.Max(1, opts.Concurrency));
                var iterationTasks = new List<Task<IterationExecutionRecord>>();

                for (int iter = 1; iter <= Math.Max(1, opts.Iterations); iter++)
                {
                    int iterIndex = iter;
                    iterationTasks.Add(Task.Run(async () =>
                    {
                        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            return await RunIterationAsync(
                                project,
                                requestIds,
                                iterIndex,
                                context.Clone(), // Isolated iteration scope!
                                opts,
                                cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }, cancellationToken));
                }

                var completedIterations = await Task.WhenAll(iterationTasks).ConfigureAwait(false);
                iterationRecords.AddRange(completedIterations);

                if (completedIterations.Any(i => i.Status is RunStatus.Failed or RunStatus.Error))
                {
                    collectionFailed = true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful cancellation caught; teardown will still execute according to policy
        }
        finally
        {
            // 5. Collection Teardown
            bool shouldRunTeardown = opts.TeardownPolicy switch
            {
                TeardownPolicy.Always => true,
                TeardownPolicy.OnFailure => collectionFailed || cancellationToken.IsCancellationRequested,
                TeardownPolicy.OnSuccess => !collectionFailed && !cancellationToken.IsCancellationRequested,
                _ => true
            };

            if (shouldRunTeardown)
            {
                foreach (var teardownId in teardownRequestIds)
                {
                    var tdAsset = FindRequestAsset(project, teardownId);
                    if (tdAsset is null) continue;

                    var tdDef = RequestAssetSerializer.FromJsonObject(tdAsset.Content);
                    var tdWatch = Stopwatch.StartNew();
                    try
                    {
                        var res = await httpService.SendAsync(tdDef, variableContext: context, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                        tdWatch.Stop();

                        var assertions = AssertionEngine.EvaluateAll(tdDef.Assertions, res);
                        bool assertionFailed = assertions.Any(a => !a.Passed);

                        ExecutionOutcome recordOutcome;
                        if (res.Outcome != ExecutionOutcome.Passed) recordOutcome = res.Outcome;
                        else if (assertionFailed) recordOutcome = ExecutionOutcome.Failed;
                        else if (res.StatusCode.HasValue && res.StatusCode.Value >= 400 && tdDef.Assertions.Count == 0) recordOutcome = ExecutionOutcome.Failed;
                        else recordOutcome = ExecutionOutcome.Passed;

                        var failMsg = assertionFailed
                            ? string.Join("; ", assertions.Where(a => !a.Passed).Select(a => $"{a.Name}: {a.Message}"))
                            : res.Error ?? (recordOutcome != ExecutionOutcome.Passed ? $"HTTP {res.StatusCode}" : null);

                        teardownResults.Add(new RequestExecutionRecord(
                            teardownId,
                            tdDef.Name,
                            0,
                            1,
                            recordOutcome,
                            res.StatusCode,
                            tdWatch.Elapsed,
                            failMsg,
                            res.Preview,
                            res.BodyPath,
                            assertions));
                    }
                    catch (Exception ex)
                    {
                        tdWatch.Stop();
                        teardownResults.Add(new RequestExecutionRecord(
                            teardownId,
                            tdDef.Name,
                            0,
                            1,
                            ExecutionOutcome.Error,
                            null,
                            tdWatch.Elapsed,
                            $"Teardown error: {ex.Message}"));
                    }
                }
            }
        }

        watch.Stop();
        var endTime = DateTimeOffset.UtcNow;

        // 6. Aggregate Status
        RunStatus finalStatus;
        if (cancellationToken.IsCancellationRequested || iterationRecords.Any(i => i.Status == RunStatus.Cancelled))
        {
            finalStatus = RunStatus.Cancelled;
        }
        else if (teardownResults.Any(t => t.Outcome == ExecutionOutcome.Error) ||
                 setupResults.Any(s => s.Outcome == ExecutionOutcome.Error) ||
                 iterationRecords.Any(i => i.Status == RunStatus.Error))
        {
            finalStatus = RunStatus.Error;
        }
        else if (teardownResults.Any(t => t.Outcome == ExecutionOutcome.Failed) ||
                 setupResults.Any(s => s.Outcome == ExecutionOutcome.Failed) ||
                 collectionFailed ||
                 iterationRecords.Any(i => i.Status == RunStatus.Failed))
        {
            finalStatus = RunStatus.Failed;
        }
        else
        {
            finalStatus = RunStatus.Passed;
        }

        int totalRequests = setupResults.Count + teardownResults.Count + iterationRecords.Sum(i => i.Requests.Count);
        int passedRequests = setupResults.Count(s => s.Outcome == ExecutionOutcome.Passed) +
                             teardownResults.Count(t => t.Outcome == ExecutionOutcome.Passed) +
                             iterationRecords.Sum(i => i.Requests.Count(r => r.Outcome == ExecutionOutcome.Passed));
        int failedRequests = totalRequests - passedRequests;

        var osArch = $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";

        return new CollectionExecutionReport(
            runId,
            collectionId,
            collectionName,
            finalStatus,
            startTime,
            endTime,
            watch.Elapsed,
            iterationRecords,
            setupResults,
            teardownResults,
            totalRequests,
            passedRequests,
            failedRequests,
            failureError,
            GitCommit: snapshot?.Commit,
            IsDirty: snapshot?.Dirty ?? false,
            SnapshotHash: snapshot?.Id,
            OsArchitecture: osArch);
    }

    private async Task<IterationExecutionRecord> RunIterationAsync(
        ProjectView project,
        IReadOnlyList<Guid> requestIds,
        int iterationIndex,
        VariableContext iterationContext,
        CollectionExecutionOptions options,
        CancellationToken cancellationToken)
    {
        var iterWatch = Stopwatch.StartNew();
        var requestRecords = new List<RequestExecutionRecord>();
        bool iterationFailed = false;
        string? iterationError = null;

        int loopBudget = 50; // Max loop budget per iteration
        int pointer = 0;

        while (pointer < requestIds.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (loopBudget <= 0)
            {
                iterationFailed = true;
                iterationError = "Execution loop budget (50) exceeded in iteration.";
                break;
            }
            loopBudget--;

            var reqId = requestIds[pointer];
            var reqAsset = FindRequestAsset(project, reqId);
            if (reqAsset is null)
            {
                pointer++;
                continue;
            }

            var reqDef = RequestAssetSerializer.FromJsonObject(reqAsset.Content);

            if (options.DelayBetweenRequestsMs > 0)
            {
                await Task.Delay(options.DelayBetweenRequestsMs, cancellationToken).ConfigureAwait(false);
            }

            var reqWatch = Stopwatch.StartNew();
            HttpExecutionResult result;
            try
            {
                result = await httpService.SendAsync(reqDef, variableContext: iterationContext, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                result = new HttpExecutionResult(ExecutionOutcome.Error, null, null, [], "", 0, false, null, reqWatch.Elapsed, ex.Message);
            }
            reqWatch.Stop();

            var assertions = AssertionEngine.EvaluateAll(reqDef.Assertions, result);
            bool assertionFailed = assertions.Any(a => !a.Passed);

            ExecutionOutcome recordOutcome;
            if (result.Outcome != ExecutionOutcome.Passed)
            {
                recordOutcome = result.Outcome;
            }
            else if (assertionFailed)
            {
                recordOutcome = ExecutionOutcome.Failed;
            }
            else if (result.StatusCode.HasValue && result.StatusCode.Value >= 400 && reqDef.Assertions.Count == 0)
            {
                recordOutcome = ExecutionOutcome.Failed;
            }
            else
            {
                recordOutcome = ExecutionOutcome.Passed;
            }

            bool isFail = recordOutcome != ExecutionOutcome.Passed;
            var failMessage = assertionFailed
                ? string.Join("; ", assertions.Where(a => !a.Passed).Select(a => $"{a.Name}: {a.Message}"))
                : result.Error ?? (isFail ? $"HTTP {result.StatusCode}" : null);

            var record = new RequestExecutionRecord(
                reqId,
                reqDef.Name,
                iterationIndex,
                1,
                recordOutcome,
                result.StatusCode,
                reqWatch.Elapsed,
                failMessage,
                result.Preview,
                result.BodyPath,
                assertions);
            requestRecords.Add(record);

            if (isFail)
            {
                iterationFailed = true;
                iterationError ??= $"Request '{reqDef.Name}' failed: {failMessage}";
                if (options.StopOnFailure)
                {
                    break;
                }
            }

            pointer++;
        }

        iterWatch.Stop();

        var finalVariables = iterationContext.RequestLocal
            .Concat(iterationContext.IterationData)
            .GroupBy(kv => kv.Key)
            .ToDictionary(g => g.Key, g => g.Last().Value);

        return new IterationExecutionRecord(
            iterationIndex,
            cancellationToken.IsCancellationRequested ? RunStatus.Cancelled : (iterationFailed ? RunStatus.Failed : RunStatus.Passed),
            iterWatch.Elapsed,
            requestRecords,
            finalVariables,
            iterationError);
    }

    private static ProjectAsset? FindRequestAsset(ProjectView project, Guid requestId)
    {
        return project.Assets.FirstOrDefault(a => a.Kind == AssetKind.Request &&
            string.Equals(a.Id, requestId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    private static List<Guid> ExtractGuidList(JsonNode? node)
    {
        var list = new List<Guid>();
        if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue val && Guid.TryParse(val.GetValue<string>(), out var id))
                {
                    list.Add(id);
                }
            }
        }
        return list;
    }
}
