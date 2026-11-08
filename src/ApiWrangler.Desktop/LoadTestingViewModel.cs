using System.Collections.ObjectModel;
using System.Net.Http;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiWrangler.Desktop;

public partial class LoadTestingViewModel : ObservableObject
{
    private static readonly HttpClient SharedHttpClient = new();
    private readonly LoadExecutionEngine engine = new(SharedHttpClient);
    private CancellationTokenSource? currentRunCts;

    [ObservableProperty] private string targetUrl = "http://127.0.0.1:5057/echo";
    [ObservableProperty] private string method = "GET";
    [ObservableProperty] private int virtualUsers = 5;
    [ObservableProperty] private int durationSeconds = 3;
    [ObservableProperty] private int targetRateRps = 20;
    [ObservableProperty] private bool confirmed = true;
    [ObservableProperty] private string loadModel = "ClosedLoopVirtualUsers";
    [ObservableProperty] private double maxP95LatencyMs = 500;
    [ObservableProperty] private double maxErrorRatePercent = 5.0;

    [ObservableProperty] private bool isRunning = false;
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private string reportSummary = "No load test executed yet.";

    [RelayCommand]
    public async Task StartLoadTestAsync()
    {
        if (IsRunning) return;
        IsRunning = true;
        Status = "Executing load test...";
        currentRunCts = new CancellationTokenSource();

        try
        {
            var model = Enum.TryParse<LoadModelType>(LoadModel, out var m) ? m : LoadModelType.ClosedLoopVirtualUsers;
            var thresholds = (MaxP95LatencyMs > 0 || MaxErrorRatePercent > 0)
                ? new LoadThresholdPolicy(
                    MaxP95LatencyMs: MaxP95LatencyMs > 0 ? MaxP95LatencyMs : null,
                    MaxErrorRatePercent: MaxErrorRatePercent > 0 ? MaxErrorRatePercent : null)
                : null;

            var config = new LoadTestConfiguration(
                TargetUrl: TargetUrl,
                Method: Method,
                ModelType: model,
                VirtualUsers: VirtualUsers,
                TargetRatePerSecond: TargetRateRps,
                Duration: TimeSpan.FromSeconds(DurationSeconds),
                RequireTargetConfirmation: true,
                Confirmed: Confirmed,
                Thresholds: thresholds
            );

            var report = await engine.ExecuteAsync(config, currentRunCts.Token);
            string thresholdResult = report.ThresholdEvaluation is null
                ? "No thresholds configured"
                : (report.ThresholdEvaluation.Passed
                    ? "PASSED (all latency and error constraints satisfied)"
                    : $"FAILED:\n  - {string.Join("\n  - ", report.ThresholdEvaluation.Violations)}");

            string errorBreakdown = report.ErrorCategories.Count > 0
                ? string.Join(", ", report.ErrorCategories.Select(kv => $"{kv.Key}: {kv.Value}"))
                : "None";

            ReportSummary = $"""
                Target: {report.TargetUrl} ({report.ModelType})
                Total Requests: {report.TotalRequests} (Success: {report.SuccessfulRequests}, Failed: {report.FailedRequests})
                Throughput: {report.AchievedRps:F1} req/s (Target: {report.TargetRps:F1} req/s)
                Target Achieved: {report.TargetLoadAchieved} | Saturated: {report.GeneratorSaturated}
                Latencies: min={report.Latency.MinMs}ms, p50={report.Latency.P50Ms}ms, p95={report.Latency.P95Ms}ms, p99={report.Latency.P99Ms}ms, max={report.Latency.MaxMs}ms
                Error Categories: {errorBreakdown}
                Threshold Evaluation: {thresholdResult}
                Duration: {report.TotalDuration.TotalSeconds:F2}s
                Abort Reason: {report.AbortReason ?? "None (Completed successfully)"}
                """;

            Status = $"Completed ({report.TotalRequests} requests in {report.TotalDuration.TotalSeconds:F1}s)";
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            ReportSummary = ex.ToString();
        }
        finally
        {
            IsRunning = false;
            currentRunCts = null;
        }
    }

    [RelayCommand]
    public void CancelLoadTest()
    {
        if (currentRunCts is not null && !currentRunCts.IsCancellationRequested)
        {
            currentRunCts.Cancel();
            Status = "Cancellation requested...";
        }
    }
}

