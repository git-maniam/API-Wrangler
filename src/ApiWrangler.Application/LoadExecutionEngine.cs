using System.Collections.Concurrent;
using System.Diagnostics;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed class LoadExecutionEngine
{
    private readonly HttpClient httpClient;

    public LoadExecutionEngine(HttpClient? client = null)
    {
        this.httpClient = client ?? new HttpClient();
    }

    public static LatencyHistogram CalculateHistogram(IReadOnlyList<TimeSpan> latencies)
    {
        if (latencies.Count == 0)
        {
            return new LatencyHistogram(
                LatenciesMs: Array.Empty<long>(),
                MinMs: 0,
                MeanMs: 0,
                MaxMs: 0,
                P50Ms: 0,
                P90Ms: 0,
                P95Ms: 0,
                P99Ms: 0
            );
        }

        var sortedMs = latencies.Select(l => (long)Math.Max(0, l.TotalMilliseconds)).OrderBy(v => v).ToList();
        double min = sortedMs[0];
        double max = sortedMs[^1];
        double mean = sortedMs.Average();

        double GetPercentile(double percentile)
        {
            int index = (int)Math.Ceiling(percentile / 100.0 * sortedMs.Count) - 1;
            index = Math.Clamp(index, 0, sortedMs.Count - 1);
            return sortedMs[index];
        }

        return new LatencyHistogram(
            LatenciesMs: sortedMs,
            MinMs: min,
            MeanMs: mean,
            MaxMs: max,
            P50Ms: GetPercentile(50),
            P90Ms: GetPercentile(90),
            P95Ms: GetPercentile(95),
            P99Ms: GetPercentile(99)
        );
    }

    public static LatencyHistogram MergeHistograms(IReadOnlyList<LatencyHistogram> histograms)
    {
        var allLatencies = histograms.SelectMany(h => h.LatenciesMs.Select(ms => TimeSpan.FromMilliseconds(ms))).ToList();
        return CalculateHistogram(allLatencies);
    }

    public static LoadThresholdEvaluation EvaluateThresholds(LoadThresholdPolicy? policy, LoadTestReport report)
    {
        if (policy is null)
        {
            return new LoadThresholdEvaluation(true, Array.Empty<string>());
        }

        var violations = new List<string>();

        if (policy.MaxP95LatencyMs.HasValue && report.Latency.P95Ms > policy.MaxP95LatencyMs.Value)
        {
            violations.Add($"p95 latency threshold exceeded: {report.Latency.P95Ms:F1}ms > {policy.MaxP95LatencyMs.Value:F1}ms");
        }

        if (policy.MaxP99LatencyMs.HasValue && report.Latency.P99Ms > policy.MaxP99LatencyMs.Value)
        {
            violations.Add($"p99 latency threshold exceeded: {report.Latency.P99Ms:F1}ms > {policy.MaxP99LatencyMs.Value:F1}ms");
        }

        double errorRate = report.TotalRequests > 0 ? (double)report.FailedRequests / report.TotalRequests * 100.0 : 0.0;
        if (policy.MaxErrorRatePercent.HasValue && errorRate > policy.MaxErrorRatePercent.Value)
        {
            violations.Add($"Error rate threshold exceeded: {errorRate:F1}% > {policy.MaxErrorRatePercent.Value:F1}%");
        }

        if (policy.MinThroughputRps.HasValue && report.AchievedRps < policy.MinThroughputRps.Value)
        {
            violations.Add($"Minimum throughput threshold failed: {report.AchievedRps:F1} rps < {policy.MinThroughputRps.Value:F1} rps");
        }

        return new LoadThresholdEvaluation(violations.Count == 0, violations);
    }

    public async Task<LoadTestReport> ExecuteAsync(
        LoadTestConfiguration config,
        CancellationToken cancellationToken = default)
    {
        if (config.RequireTargetConfirmation && !config.Confirmed)
        {
            throw new InvalidOperationException("Large load test requires explicit target confirmation before execution.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();

        var metrics = new ConcurrentBag<LoadIterationMetric>();
        var errorCounts = new ConcurrentDictionary<string, int>();
        int consecutiveErrors = 0;
        string? abortReason = null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (config.Duration > TimeSpan.Zero)
        {
            cts.CancelAfter(config.Duration);
        }

        if (config.ModelType == LoadModelType.ClosedLoopVirtualUsers)
        {
            // Closed-loop virtual users execution
            int userCount = Math.Max(1, config.VirtualUsers);
            var tasks = new List<Task>();

            for (int u = 0; u < userCount; u++)
            {
                int userId = u + 1;
                tasks.Add(Task.Run(async () =>
                {
                    int iteration = 0;
                    while (!cts.Token.IsCancellationRequested)
                    {
                        iteration++;
                        var reqSw = Stopwatch.StartNew();
                        bool success = false;
                        int statusCode = 0;
                        string? errorCat = null;

                        try
                        {
                            using var req = new HttpRequestMessage(new HttpMethod(config.Method), config.TargetUrl);
                            if (config.Headers is not null)
                            {
                                foreach (var (k, v) in config.Headers) req.Headers.TryAddWithoutValidation(k, v);
                            }
                            if (!string.IsNullOrEmpty(config.Body))
                            {
                                req.Content = new StringContent(config.Body);
                            }

                            using var resp = await httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
                            statusCode = (int)resp.StatusCode;
                            success = resp.IsSuccessStatusCode;
                            if (!success)
                            {
                                errorCat = $"HTTP {statusCode}";
                            }
                        }
                        catch (OperationCanceledException) when (cts.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            success = false;
                            errorCat = ex.GetType().Name;
                        }

                        reqSw.Stop();
                        metrics.Add(new LoadIterationMetric(
                            IterationId: iteration,
                            VirtualUserId: userId,
                            StartTimestampTicks: Stopwatch.GetTimestamp(),
                            Latency: reqSw.Elapsed,
                            StatusCode: statusCode,
                            Success: success,
                            ErrorCategory: errorCat
                        ));

                        if (!success)
                        {
                            Interlocked.Increment(ref consecutiveErrors);
                            if (errorCat is not null) errorCounts.AddOrUpdate(errorCat, 1, (_, c) => c + 1);

                            // Safety thresholds check
                            if (consecutiveErrors >= config.MaxConsecutiveErrors)
                            {
                                abortReason = $"Aborted: reached {config.MaxConsecutiveErrors} consecutive errors.";
                                cts.Cancel();
                                break;
                            }
                        }
                        else
                        {
                            Interlocked.Exchange(ref consecutiveErrors, 0);
                        }

                        // Think time
                        if (config.ThinkTime > TimeSpan.Zero && !cts.Token.IsCancellationRequested)
                        {
                            try
                            {
                                await Task.Delay(config.ThinkTime, cts.Token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                        }
                    }
                }));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        else
        {
            // Open-loop arrival rate model
            int targetRate = Math.Max(1, config.TargetRatePerSecond);
            var interval = TimeSpan.FromSeconds(1.0 / targetRate);
            var activeTasks = new ConcurrentBag<Task>();
            int arrivalIndex = 0;

            while (!cts.Token.IsCancellationRequested)
            {
                arrivalIndex++;
                int currentArrival = arrivalIndex;

                activeTasks.Add(Task.Run(async () =>
                {
                    var reqSw = Stopwatch.StartNew();
                    bool success = false;
                    int statusCode = 0;
                    string? errorCat = null;

                    try
                    {
                        using var req = new HttpRequestMessage(new HttpMethod(config.Method), config.TargetUrl);
                        using var resp = await httpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
                        statusCode = (int)resp.StatusCode;
                        success = resp.IsSuccessStatusCode;
                        if (!success) errorCat = $"HTTP {statusCode}";
                    }
                    catch (OperationCanceledException) when (cts.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        success = false;
                        errorCat = ex.GetType().Name;
                    }

                    reqSw.Stop();
                    metrics.Add(new LoadIterationMetric(
                        IterationId: currentArrival,
                        VirtualUserId: 0,
                        StartTimestampTicks: Stopwatch.GetTimestamp(),
                        Latency: reqSw.Elapsed,
                        StatusCode: statusCode,
                        Success: success,
                        ErrorCategory: errorCat
                    ));

                    if (!success && errorCat is not null)
                    {
                        errorCounts.AddOrUpdate(errorCat, 1, (_, c) => c + 1);
                    }
                }));

                try
                {
                    await Task.Delay(interval, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await Task.WhenAll(activeTasks).ConfigureAwait(false);
        }

        sw.Stop();
        var completedAt = DateTimeOffset.UtcNow;
        var allMetrics = metrics.ToList();
        int totalRequests = allMetrics.Count;
        int successful = allMetrics.Count(m => m.Success);
        int failed = totalRequests - successful;

        double elapsedSeconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        double achievedRps = totalRequests / elapsedSeconds;
        double targetRps = config.ModelType == LoadModelType.OpenLoopArrivalRate
            ? config.TargetRatePerSecond
            : (config.VirtualUsers / Math.Max(0.001, (config.ThinkTime.TotalSeconds + 0.05)));

        bool targetAchieved = achievedRps >= (targetRps * 0.85); // 85% of target
        bool generatorSaturated = achievedRps < (targetRps * 0.70) && failed == 0; // If target was missed without target server errors, generator was saturated

        var histogram = CalculateHistogram(allMetrics.Select(m => m.Latency).ToList());

        var preliminaryReport = new LoadTestReport(
            TargetUrl: config.TargetUrl,
            ModelType: config.ModelType,
            ProfileType: config.ProfileType,
            StartedAt: startedAt,
            CompletedAt: completedAt,
            TotalDuration: sw.Elapsed,
            TotalRequests: totalRequests,
            SuccessfulRequests: successful,
            FailedRequests: failed,
            AchievedRps: achievedRps,
            TargetRps: targetRps,
            TargetLoadAchieved: targetAchieved,
            GeneratorSaturated: generatorSaturated,
            AbortReason: abortReason,
            ErrorCategories: errorCounts.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            Latency: histogram,
            ThresholdEvaluation: null
        );

        var thresholdEvaluation = EvaluateThresholds(config.Thresholds, preliminaryReport);

        return preliminaryReport with { ThresholdEvaluation = thresholdEvaluation };
    }
}

