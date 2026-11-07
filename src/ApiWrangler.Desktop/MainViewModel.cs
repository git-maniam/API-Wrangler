using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiWrangler.Desktop;

public partial class MainViewModel(HttpRequestService requests, ProjectEditorViewModel projectEditor) : ObservableObject
{
    public ProjectEditorViewModel ProjectEditor { get; } = projectEditor;

    [ObservableProperty] private string requestName = "Untitled request";
    [ObservableProperty] private string requestDescription = "";
    [ObservableProperty] private string method = "GET";
    [ObservableProperty] private string url = "http://localhost:5057/echo";
    [ObservableProperty] private string requestQuery = "";
    [ObservableProperty] private string requestHeaders = "Accept: application/json";
    [ObservableProperty] private string requestBody = "";
    [ObservableProperty] private string bodyMode = "raw";
    [ObservableProperty] private string bodyFilePath = "";
    [ObservableProperty] private bool followRedirects;
    [ObservableProperty] private bool dangerousIgnoreTlsErrors;
    [ObservableProperty] private string responseBody = "Start the local fixture server, then send a request.";
    [ObservableProperty] private string responseHeaders = "";
    [ObservableProperty] private string responseCookies = "";
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private string artifactPath = "";
    [ObservableProperty] private ProjectAsset? selectedProjectRequest;
    [ObservableProperty] private ProjectAsset? selectedEnvironment;
    [ObservableProperty] private string variablesInspectionText = "";

    public PagedDataset Dataset { get; } = new();
    public string[] Methods { get; } = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];
    public string[] BodyModes { get; } = ["raw", "none", "urlencoded", "form-data", "file"];

    public Guid? CurrentRequestId { get; private set; }
    public string? CurrentRequestPath { get; private set; }

    public IEnumerable<ProjectAsset> ProjectRequests =>
        ProjectEditor.Assets.Where(a => a.Kind == AssetKind.Request);

    public IEnumerable<ProjectAsset> ProjectEnvironments =>
        ProjectEditor.Assets.Where(a => a.Kind == AssetKind.Environment);

    [RelayCommand]
    private void NewRequest()
    {
        CurrentRequestId = Guid.NewGuid();
        CurrentRequestPath = null;
        RequestName = "New request";
        RequestDescription = "";
        Method = "GET";
        Url = "http://localhost:5057/echo";
        RequestQuery = "";
        RequestHeaders = "Accept: application/json";
        RequestBody = "";
        BodyMode = "raw";
        BodyFilePath = "";
        ResponseBody = "";
        ResponseHeaders = "";
        ResponseCookies = "";
        Status = "New request created.";
    }

    [RelayCommand]
    private void CloseRequest()
    {
        CurrentRequestId = null;
        CurrentRequestPath = null;
        RequestName = "";
        RequestDescription = "";
        Url = "";
        RequestQuery = "";
        RequestHeaders = "";
        RequestBody = "";
        ResponseBody = "";
        ResponseHeaders = "";
        ResponseCookies = "";
        Status = "Workbench closed.";
    }

    [RelayCommand]
    private async Task SaveRequestAsync()
    {
        var session = ProjectEditor.CurrentSession;
        if (session is null)
        {
            Status = "Open a project first to save this request.";
            return;
        }

        try
        {
            var req = BuildDefinition();
            var json = RequestAssetSerializer.ToJsonObject(req);
            var path = CurrentRequestPath;

            if (string.IsNullOrEmpty(path))
            {
                var view = await session.ReadAsync();
                var manifest = view.Assets.Single(a => a.Kind == AssetKind.Manifest);
                var collDir = manifest.Content["paths"]?["collections"]?.GetValue<string>() ?? "collections";
                var slug = string.Concat(req.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_')).Trim('_').ToLowerInvariant();
                if (string.IsNullOrEmpty(slug)) slug = "request";
                path = $"{collDir}/{slug}.request.json";
            }

            var currentAsset = (await session.ReadAsync()).Assets.FirstOrDefault(a => a.Path == path);
            await session.SaveAsync([new AssetEdit(path, json, currentAsset?.Hash)]);
            CurrentRequestPath = path;
            CurrentRequestId = req.Id;
            await ProjectEditor.RefreshProjectAsync();
            OnPropertyChanged(nameof(ProjectRequests));
            Status = $"Saved request to {path}.";
        }
        catch (Exception ex)
        {
            Status = $"Failed to save request: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadRequestAsync(ProjectAsset? asset)
    {
        var target = asset ?? SelectedProjectRequest;
        if (target is null || target.Kind != AssetKind.Request)
        {
            Status = "Select a request asset to load.";
            return;
        }

        try
        {
            var req = RequestAssetSerializer.FromJsonObject(target.Content);
            CurrentRequestId = req.Id;
            CurrentRequestPath = target.Path;
            RequestName = req.Name;
            RequestDescription = req.Description ?? "";
            Method = req.Method;
            Url = req.Url;
            RequestQuery = string.Join(Environment.NewLine, req.Query.Where(q => q.Enabled).Select(q => $"{q.Name}={q.Value}"));
            RequestHeaders = string.Join(Environment.NewLine, req.Headers.Where(h => h.Enabled).Select(h => $"{h.Name}: {h.Value}"));
            if (req.BodyDefinition is not null)
            {
                BodyMode = req.BodyDefinition.Mode;
                RequestBody = req.BodyDefinition.Text ?? "";
                BodyFilePath = req.BodyDefinition.FilePath ?? "";
            }
            else
            {
                BodyMode = "none";
                RequestBody = "";
                BodyFilePath = "";
            }
            Status = $"Loaded request: {req.Name} ({target.Path})";
        }
        catch (Exception ex)
        {
            Status = $"Could not load request: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task InspectVariablesAsync()
    {
        try
        {
            var resolver = new VariableResolver();
            var context = BuildVariableContext();
            var target = $"{Url} {RequestHeaders} {RequestQuery} {RequestBody}";
            var inspections = await resolver.InspectAsync(target, context);
            if (inspections.Count == 0)
            {
                VariablesInspectionText = "No variable expressions found in this request.";
            }
            else
            {
                VariablesInspectionText = string.Join(Environment.NewLine, inspections.Select(i =>
                    $"{i.Name} -> {i.RedactedValue} (Source: {i.Source}{(i.IsSecret ? ", Secret" : "")})"));
            }
            Status = $"Inspected {inspections.Count} variable(s).";
        }
        catch (Exception ex)
        {
            VariablesInspectionText = $"Inspection error: {ex.Message}";
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SendAsync(CancellationToken token)
    {
        try
        {
            var request = BuildDefinition();
            var varContext = BuildVariableContext();
            Status = "Sending…";
            ArtifactPath = "";
            // Even validation and synchronous transport setup stay off the dispatcher.
            var result = await Task.Run(() => requests.SendAsync(request, variableContext: varContext, cancellationToken: token), token);
            Status = $"{result.Outcome} · HTTP {result.StatusCode?.ToString() ?? "—"} · {result.Duration.TotalMilliseconds:F0} ms · {result.BytesReceived:N0} bytes";
            ResponseBody = result.Error ?? result.Preview;
            if (result.PreviewTruncated) Status += " · preview truncated";
            ResponseHeaders = string.Join(Environment.NewLine, result.Headers.Select(h => $"{h.Name}: {h.Value}"));
            if (result.Cookies is { Count: > 0 })
            {
                ResponseCookies = string.Join(Environment.NewLine, result.Cookies.Select(c => $"{c.Name}={c.Value} (Domain={c.Domain ?? "*"}, Path={c.Path ?? "/"})"));
            }
            else
            {
                ResponseCookies = "(No cookies received)";
            }
            ArtifactPath = result.BodyPath ?? "";
        }
        catch (VariableResolutionException exception) { Status = exception.Message; }
        catch (ArgumentException exception) { Status = exception.Message; }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception) { Status = "Request could not complete. Check local artifact storage and request settings."; }
    }

    private VariableContext BuildVariableContext()
    {
        var context = new VariableContext();
        if (SelectedEnvironment?.Content["variables"] is JsonObject envVars)
        {
            foreach (var kvp in envVars)
            {
                if (kvp.Value is JsonValue val)
                {
                    context.Environment[kvp.Key] = val.ToString();
                }
            }
        }
        return context;
    }

    private HttpRequestDefinition BuildDefinition()
    {
        var headers = RequestHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'))
            .Select(line =>
            {
                var colon = line.IndexOf(':');
                if (colon < 1) throw new ArgumentException("Use one Name: value header per line.");
                return new HeaderEntry(line[..colon].Trim(), line[(colon + 1)..].Trim());
            }).ToArray();

        var queryList = new List<QueryParamEntry>();
        if (!string.IsNullOrWhiteSpace(RequestQuery))
        {
            foreach (var line in RequestQuery.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;
                var eq = trimmed.IndexOf('=');
                if (eq < 0) queryList.Add(new QueryParamEntry(trimmed, ""));
                else queryList.Add(new QueryParamEntry(trimmed[..eq].Trim(), trimmed[(eq + 1)..].Trim()));
            }
        }

        RequestBodyDefinition? bodyDef = null;
        if (!string.Equals(BodyMode, "none", StringComparison.OrdinalIgnoreCase))
        {
            bodyDef = new RequestBodyDefinition
            {
                Mode = BodyMode,
                Text = string.IsNullOrEmpty(RequestBody) ? null : RequestBody,
                FilePath = string.IsNullOrEmpty(BodyFilePath) ? null : BodyFilePath
            };
        }

        return new HttpRequestDefinition
        {
            Id = CurrentRequestId ?? Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(RequestName) ? "Untitled request" : RequestName,
            Description = RequestDescription,
            Method = Method,
            Url = Url,
            Headers = headers,
            Query = queryList,
            BodyDefinition = bodyDef,
            TransportOptions = new HttpTransportOptions
            {
                FollowRedirects = FollowRedirects,
                DangerousIgnoreTlsErrors = DangerousIgnoreTlsErrors
            }
        };
    }
}
