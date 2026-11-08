using System.Collections.ObjectModel;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiWrangler.Desktop;

public partial class MockServerViewModel : ObservableObject, IAsyncDisposable
{
    private MockEngine? engine;

    [ObservableProperty] private string host = "127.0.0.1";
    [ObservableProperty] private int port = 5055;
    [ObservableProperty] private bool allowExternalBinding = false;
    [ObservableProperty] private string serverStatus = "Stopped";
    [ObservableProperty] private string listeningUrl = "";
    [ObservableProperty] private bool isRunning = false;

    // Rule creation
    [ObservableProperty] private string newRuleName = "Get User Profile";
    [ObservableProperty] private string newRuleMethod = "GET";
    [ObservableProperty] private string newRulePath = "/api/v1/users/{id}";
    [ObservableProperty] private int newRuleStatus = 200;
    [ObservableProperty] private string newRuleBody = "{\"id\": 1, \"name\": \"Alice\", \"role\": \"developer\"}";
    [ObservableProperty] private int newRuleDelayMs = 0;
    [ObservableProperty] private string newRuleFault = "None";

    [ObservableProperty] private string logsSummary = "0 requests logged.";
    [ObservableProperty] private string rawLogs = "";

    public ObservableCollection<MockRule> ConfiguredRules { get; } = new();

    public MockServerViewModel()
    {
        // Add a default fixture rule
        var defaultRule = new MockRule(
            Id: "rule-1",
            Name: "Default Status Endpoint",
            Method: "GET",
            PathPattern: "/api/status",
            Priority: 10,
            ResponseStatusCode: 200,
            ResponseBody: "{\"status\": \"healthy\", \"server\": \"ApiWrangler Mock\"}"
        );
        ConfiguredRules.Add(defaultRule);
    }

    [RelayCommand]
    public void AddRule()
    {
        var fault = Enum.TryParse<MockFaultType>(NewRuleFault, out var f) ? f : MockFaultType.None;
        var rule = new MockRule(
            Id: Guid.NewGuid().ToString("N"),
            Name: NewRuleName,
            Method: NewRuleMethod,
            PathPattern: NewRulePath,
            Priority: 10,
            ResponseStatusCode: NewRuleStatus,
            ResponseBody: NewRuleBody,
            ResponseDelay: TimeSpan.FromMilliseconds(NewRuleDelayMs),
            FaultType: fault
        );

        ConfiguredRules.Add(rule);
        engine?.AddRule(rule);
    }

    [RelayCommand]
    public async Task ToggleServerAsync()
    {
        if (IsRunning)
        {
            await StopServerAsync();
        }
        else
        {
            await StartServerAsync();
        }
    }

    public async Task StartServerAsync()
    {
        try
        {
            ServerStatus = "Starting...";
            engine = new MockEngine();

            foreach (var rule in ConfiguredRules)
            {
                engine.AddRule(rule);
            }

            var cfg = new MockServerConfiguration(
                Host: Host,
                Port: Port,
                AllowExternalBinding: AllowExternalBinding
            );

            await engine.StartAsync(cfg);
            ListeningUrl = engine.ListeningUrl ?? $"http://{Host}:{Port}";
            ServerStatus = $"Running on {ListeningUrl}";
            IsRunning = true;
        }
        catch (Exception ex)
        {
            ServerStatus = $"Start failed: {ex.Message}";
            IsRunning = false;
        }
    }

    public async Task StopServerAsync()
    {
        if (engine is not null)
        {
            await engine.StopAsync();
            RefreshLogs();
            engine = null;
        }

        ListeningUrl = "";
        ServerStatus = "Stopped";
        IsRunning = false;
    }

    [RelayCommand]
    public void RefreshLogs()
    {
        if (engine is null) return;
        var logs = engine.Logs;
        LogsSummary = $"{logs.Count} requests logged.";
        RawLogs = string.Join("\n", logs.Select(l =>
            $"[{l.Timestamp:HH:mm:ss.fff}] {l.Method} {l.Path}{l.QueryString} -> {l.ResponseStatusCode} ({l.Duration.TotalMilliseconds:F1}ms) Rule:{l.MatchedRuleId} Fault:{l.FaultApplied}"));
    }

    [RelayCommand]
    public void ResetState()
    {
        engine?.ResetState();
        RefreshLogs();
    }

    public async ValueTask DisposeAsync()
    {
        await StopServerAsync();
    }
}

