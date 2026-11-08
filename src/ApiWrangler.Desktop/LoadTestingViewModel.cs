using System.Collections.ObjectModel;
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
            var config = new LoadTestConfiguration(
                TargetUrl: TargetUrl,
                Method: Method,
                ModelType: model,
                VirtualUsers: VirtualUsers,
                TargetRatePerSecond: TargetRateRps,
                Duration: TimeSpan.FromSeconds(DurationSeconds),
                RequireTargetConfirmation: true,
                Confirmed: Confirmed
            );

            var report = await engine.ExecuteAsync(config, currentRunCts.Token);
            ReportSummary = $"""
                Target: {report.TargetUrl} ({report.ModelType})
                Total Requests: {report.TotalRequests} (Success: {report.SuccessfulRequests}, Failed: {report.FailedRequests})
                Throughput: {report.AchievedRps:F1} req/s (Target: {report.TargetRps:F1} req/s)
                Target Achieved: {report.TargetLoadAchieved} | Saturated: {report.GeneratorSaturated}
                Latencies: min={report.Latency.MinMs}ms, p50={report.Latency.P50Ms}ms, p95={report.Latency.P95Ms}ms, p99={report.Latency.P99Ms}ms, max={report.Latency.MaxMs}ms
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

