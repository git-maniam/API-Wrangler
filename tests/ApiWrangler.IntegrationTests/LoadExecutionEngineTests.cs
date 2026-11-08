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
}

