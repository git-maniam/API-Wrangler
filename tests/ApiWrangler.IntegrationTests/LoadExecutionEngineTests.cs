using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class LoadExecutionEngineTests
{
    [Fact]
    public void LoadEngine_CalculateHistogram_CalculatesAccuratePercentiles()
    {
        // 100 sample latencies from 1ms to 100ms
        var latencies = Enumerable.Range(1, 100).Select(i => TimeSpan.FromMilliseconds(i)).ToList();
        var hist = LoadExecutionEngine.CalculateHistogram(latencies);

        Assert.Equal(1, hist.MinMs);
        Assert.Equal(100, hist.MaxMs);
        Assert.Equal(50.5, hist.MeanMs);
        Assert.Equal(50, hist.P50Ms);
        Assert.Equal(90, hist.P90Ms);
        Assert.Equal(95, hist.P95Ms);
        Assert.Equal(99, hist.P99Ms);
    }

    [Fact]
    public async Task LoadEngine_UnconfirmedRun_ThrowsException()
    {
        var engine = new LoadExecutionEngine();
        var config = new LoadTestConfiguration(
            TargetUrl: "http://127.0.0.1:5057/echo",
            RequireTargetConfirmation: true,
            Confirmed: false
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.ExecuteAsync(config));
    }

    [Fact]
    public async Task LoadEngine_ClosedLoopExecution_ExecutesVUsAndReportsMetrics()
    {
        await using var mock = new MockEngine();
        mock.AddRule(new MockRule(
            Id: "load-target",
            Name: "Fast Target",
            Method: "GET",
            PathPattern: "/api/fast",
            ResponseStatusCode: 200,
            ResponseBody: "{\"ok\": true}"
        ));
        await mock.StartAsync();

        try
        {
            var engine = new LoadExecutionEngine();
            var config = new LoadTestConfiguration(
                TargetUrl: mock.ListeningUrl + "/api/fast",
                Method: "GET",
                ModelType: LoadModelType.ClosedLoopVirtualUsers,
                VirtualUsers: 3,
                Duration: TimeSpan.FromMilliseconds(500),
                RequireTargetConfirmation: true,
                Confirmed: true
            );

            var report = await engine.ExecuteAsync(config);

            Assert.True(report.TotalRequests > 0);
            Assert.Equal(report.TotalRequests, report.SuccessfulRequests);
            Assert.Equal(0, report.FailedRequests);
            Assert.True(report.AchievedRps > 0);
            Assert.NotNull(report.Latency);
            Assert.True(report.Latency.P50Ms >= 0);
        }
        finally
        {
            await mock.StopAsync();
        }
    }

    [Fact]
    public async Task LoadEngine_Thresholds_AbortsOnConsecutiveErrors()
    {
        await using var mock = new MockEngine();
        mock.AddRule(new MockRule(
            Id: "broken-target",
            Name: "Error Target",
            Method: "GET",
            PathPattern: "/api/error",
            ResponseStatusCode: 500,
            ResponseBody: "{\"error\": \"Crash\"}"
        ));
        await mock.StartAsync();

        try
        {
            var engine = new LoadExecutionEngine();
            var config = new LoadTestConfiguration(
                TargetUrl: mock.ListeningUrl + "/api/error",
                Method: "GET",
                ModelType: LoadModelType.ClosedLoopVirtualUsers,
                VirtualUsers: 1,
                Duration: TimeSpan.FromSeconds(5),
                MaxConsecutiveErrors: 5,
                RequireTargetConfirmation: true,
                Confirmed: true
            );

            var report = await engine.ExecuteAsync(config);

            Assert.NotNull(report.AbortReason);
            Assert.Contains("consecutive errors", report.AbortReason);
            Assert.True(report.FailedRequests >= 5);
        }
        finally
        {
            await mock.StopAsync();
        }
    }

    [Fact]
    public async Task LoadEngine_Cancellation_StopsCleanlyWithoutLeakingTasks()
    {
        await using var mock = new MockEngine();
        mock.AddRule(new MockRule(
            Id: "slow-target",
            Name: "Slow Target",
            Method: "GET",
            PathPattern: "/api/slow",
            ResponseDelay: TimeSpan.FromMilliseconds(100),
            ResponseStatusCode: 200
        ));
        await mock.StartAsync();

        try
        {
            var engine = new LoadExecutionEngine();
            using var cts = new CancellationTokenSource();

            var config = new LoadTestConfiguration(
                TargetUrl: mock.ListeningUrl + "/api/slow",
                Duration: TimeSpan.FromSeconds(10),
                VirtualUsers: 2,
                RequireTargetConfirmation: true,
                Confirmed: true
            );

            var task = engine.ExecuteAsync(config, cts.Token);
            await Task.Delay(200); // Allow test to start
            cts.Cancel(); // Cancel mid-run

            var report = await task;
            Assert.True(report.TotalDuration < TimeSpan.FromSeconds(5));
        }
        finally
        {
            await mock.StopAsync();
        }
    }

    [Fact]
    public void LoadEngine_MergeHistograms_MergesAccuratePercentilesFromCombinedPool()
    {
        var h1 = LoadExecutionEngine.CalculateHistogram(Enumerable.Repeat(TimeSpan.FromMilliseconds(10), 50).ToList());
        var h2 = LoadExecutionEngine.CalculateHistogram(Enumerable.Repeat(TimeSpan.FromMilliseconds(100), 50).ToList());

        var merged = LoadExecutionEngine.MergeHistograms(new[] { h1, h2 });

        Assert.Equal(100, merged.LatenciesMs.Count);
        Assert.Equal(10, merged.MinMs);
        Assert.Equal(100, merged.MaxMs);
        Assert.Equal(55, merged.MeanMs);
        Assert.Equal(10, merged.P50Ms);
        Assert.Equal(100, merged.P90Ms);
        Assert.Equal(100, merged.P95Ms);
        Assert.Equal(100, merged.P99Ms);
    }

    [Fact]
    public void LoadEngine_EvaluateThresholds_EvaluatesPassAndDistinctViolations()
    {
        var now = DateTimeOffset.UtcNow;
        var sampleReport = new LoadTestReport(
            TargetUrl: "http://127.0.0.1:5000/api",
            ModelType: LoadModelType.ClosedLoopVirtualUsers,
            ProfileType: LoadProfileType.FixedUsers,
            StartedAt: now.AddSeconds(-4),
            CompletedAt: now,
            TotalDuration: TimeSpan.FromSeconds(4),
            TotalRequests: 100,
            SuccessfulRequests: 90,
            FailedRequests: 10,
            AchievedRps: 25.0,
            TargetRps: 50.0,
            TargetLoadAchieved: false,
            GeneratorSaturated: false,
            AbortReason: null,
            ErrorCategories: new Dictionary<string, int> { ["HTTP 500"] = 10 },
            Latency: new LatencyHistogram(
                LatenciesMs: Array.Empty<long>(),
                MinMs: 10,
                MeanMs: 50,
                MaxMs: 250,
                P50Ms: 40,
                P90Ms: 120,
                P95Ms: 180,
                P99Ms: 240
            )
        );

        // Case 1: All thresholds satisfied
        var passingPolicy = new LoadThresholdPolicy(
            MaxP95LatencyMs: 200,
            MaxP99LatencyMs: 300,
            MaxErrorRatePercent: 15.0,
            MinThroughputRps: 20.0
        );
        var passEval = LoadExecutionEngine.EvaluateThresholds(passingPolicy, sampleReport);
        Assert.True(passEval.Passed);
        Assert.Empty(passEval.Violations);

        // Case 2: All thresholds violated
        var strictPolicy = new LoadThresholdPolicy(
            MaxP95LatencyMs: 100, // actual 180
            MaxP99LatencyMs: 200, // actual 240
            MaxErrorRatePercent: 5.0, // actual 10%
            MinThroughputRps: 30.0 // actual 25
        );
        var failEval = LoadExecutionEngine.EvaluateThresholds(strictPolicy, sampleReport);
        Assert.False(failEval.Passed);
        Assert.Equal(4, failEval.Violations.Count);
        Assert.Contains(failEval.Violations, v => v.Contains("p95 latency threshold exceeded"));
        Assert.Contains(failEval.Violations, v => v.Contains("p99 latency threshold exceeded"));
        Assert.Contains(failEval.Violations, v => v.Contains("Error rate threshold exceeded"));
        Assert.Contains(failEval.Violations, v => v.Contains("Minimum throughput threshold failed"));
    }

    [Fact]
    public async Task LoadEngine_FailureCategorization_DistinguishesFunctionalVsTransportVsThresholdFailures()
    {
        await using var mock = new MockEngine();
        mock.AddRule(new MockRule(
            Id: "err-500",
            Name: "Error 500",
            Method: "GET",
            PathPattern: "/api/fault500",
            ResponseStatusCode: 500,
            ResponseBody: "{\"error\":\"Internal Server Error\"}"
        ));
        mock.AddRule(new MockRule(
            Id: "abort-conn",
            Name: "Abort Connection",
            Method: "GET",
            PathPattern: "/api/abort",
            FaultType: MockFaultType.ConnectionTermination
        ));
        await mock.StartAsync();

        try
        {
            var engine = new LoadExecutionEngine();

            // 1. Functional HTTP 500 error & Threshold test
            var funcConfig = new LoadTestConfiguration(
                TargetUrl: mock.ListeningUrl + "/api/fault500",
                VirtualUsers: 1,
                Duration: TimeSpan.FromMilliseconds(1000),
                RequireTargetConfirmation: true,
                Confirmed: true,
                Thresholds: new LoadThresholdPolicy(MaxErrorRatePercent: 0) // zero tolerance threshold
            );

            var funcReport = await engine.ExecuteAsync(funcConfig);
            Assert.True(funcReport.FailedRequests > 0);
            Assert.Contains("HTTP 500", funcReport.ErrorCategories.Keys);
            Assert.NotNull(funcReport.ThresholdEvaluation);
            Assert.False(funcReport.ThresholdEvaluation.Passed);
            Assert.Contains(funcReport.ThresholdEvaluation.Violations, v => v.Contains("Error rate threshold exceeded"));

            // 2. Transport connection termination test
            var transportConfig = new LoadTestConfiguration(
                TargetUrl: mock.ListeningUrl + "/api/abort",
                VirtualUsers: 1,
                Duration: TimeSpan.FromMilliseconds(1000),
                RequireTargetConfirmation: true,
                Confirmed: true
            );

            var transportReport = await engine.ExecuteAsync(transportConfig);
            Assert.True(transportReport.FailedRequests > 0);
            Assert.Contains(transportReport.ErrorCategories.Keys, k => k.Contains("Http") || k.Contains("Socket") || k.Contains("IO"));
            Assert.DoesNotContain("HTTP 500", transportReport.ErrorCategories.Keys);
        }
        finally
        {
            await mock.StopAsync();
        }
    }
}

