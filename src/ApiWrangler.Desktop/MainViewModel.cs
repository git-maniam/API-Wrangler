using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiWrangler.Desktop;

public partial class MainViewModel(
    HttpRequestService requests,
    ProjectEditorViewModel projectEditor,
    LocalSettingsStore? settingsStore = null) : ObservableObject
{
    private readonly LocalSettingsStore settingsStore = settingsStore ?? new LocalSettingsStore();
    public ProjectEditorViewModel ProjectEditor { get; } = projectEditor;
    public DatasetImportViewModel DatasetImport { get; } = new(() => projectEditor.CurrentSession);
    public DatasetGridViewModel DatasetGrid { get; } = new();

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
    [ObservableProperty] private ProjectAsset? selectedCollection;
    [ObservableProperty] private string collectionExecutionStatus = "";
    [ObservableProperty] private CollectionExecutionReport? latestCollectionReport;
    [ObservableProperty] private string variablesInspectionText = "";
    [ObservableProperty] private string authType = "none";
    [ObservableProperty] private string authUsername = "";
    [ObservableProperty] private string authPassword = "";
    [ObservableProperty] private string authToken = "";
    [ObservableProperty] private string authApiKeyName = "X-Api-Key";
    [ObservableProperty] private string authApiKeyValue = "";
    [ObservableProperty] private string authApiKeyLocation = "header";

    // P2.4 Structured formatting and typography
    [ObservableProperty] private string editorFontFamily = "Segoe UI";
    [ObservableProperty] private double editorFontSize = 16.0;
    [ObservableProperty] private string responseViewMode = "Pretty";
    [ObservableProperty] private string responseDiagnostic = "";
    [ObservableProperty] private string rawResponseBody = "";
    [ObservableProperty] private string responseHighlighting = "Json";
    [ObservableProperty] private string requestHighlighting = "Json";
    [ObservableProperty] private string requestBodyDiagnostic = "";

    public string[] AvailableFontFamilies { get; } = ["Segoe UI", "Cascadia Code", "Consolas", "Courier New", "Lucida Console", "Fira Code"];
    public double[] AvailableFontSizes { get; } = [10, 11, 12, 13, 14, 15, 16, 18, 20, 24];
    public string[] ResponseViewModes { get; } = ["Pretty", "Raw"];

    public Visibility RequestBodyDiagnosticVisibility =>
        string.IsNullOrEmpty(RequestBodyDiagnostic) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ResponseDiagnosticVisibility =>
        string.IsNullOrEmpty(ResponseDiagnostic) ? Visibility.Collapsed : Visibility.Visible;

    public PagedDataset Dataset { get; } = new();
    public string[] Methods { get; } = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];
    public string[] BodyModes { get; } = ["raw", "none", "urlencoded", "form-data", "file"];
    public string[] AuthTypes { get; } = ["none", "basic", "bearer", "apiKey", "oauth2", "windows", "certificate"];
    public string[] ApiKeyLocations { get; } = ["header", "query"];

    public Guid? CurrentRequestId { get; private set; }
    public string? CurrentRequestPath { get; private set; }

    public IEnumerable<ProjectAsset> ProjectRequests =>
        ProjectEditor.Assets.Where(a => a.Kind == AssetKind.Request);

    public IEnumerable<ProjectAsset> ProjectEnvironments =>
        ProjectEditor.Assets.Where(a => a.Kind == AssetKind.Environment);

    public IEnumerable<ProjectAsset> ProjectCollections =>
        ProjectEditor.Assets.Where(a => a.Kind == AssetKind.Collection);

    [RelayCommand]
    private async Task RunCollectionAsync(CancellationToken token)
    {
        var target = SelectedCollection;
        var session = ProjectEditor.CurrentSession;
        if (target is null || session is null || !Guid.TryParse(target.Id, out var collectionId))
        {
            Status = "Select a collection from an open project to run.";
            return;
        }

        try
        {
            Status = $"Running collection {target.Name}…";
            var runner = new CollectionRunner(requests);
            var varContext = BuildVariableContext();

            var snapshot = await session.SnapshotAsync(token);
            await using var snapSession = await ProjectEditor.Repository.OpenAsync(snapshot.Directory, writable: false, token: token);
            var snapView = await snapSession.ReadAsync(token);

            var report = await runner.ExecuteCollectionAsync(snapView, collectionId, baseContext: varContext, snapshot: snapshot, cancellationToken: token);
            LatestCollectionReport = report;
            CollectionExecutionStatus = $"{report.Status}: {report.PassedRequests}/{report.TotalRequestsRun} requests passed ({report.Duration.TotalMilliseconds:F0} ms).";
            Status = $"Collection run {report.Status}: {report.PassedRequests}/{report.TotalRequestsRun} passed.";
        }
        catch (OperationCanceledException)
        {
            Status = "Collection run cancelled.";
        }
        catch (Exception ex)
        {
            Status = $"Collection run failed: {ex.Message}";
        }
    }

    partial void OnRequestBodyDiagnosticChanged(string value) =>
        OnPropertyChanged(nameof(RequestBodyDiagnosticVisibility));

    partial void OnResponseDiagnosticChanged(string value) =>
        OnPropertyChanged(nameof(ResponseDiagnosticVisibility));

    partial void OnEditorFontFamilyChanged(string value) =>
        _ = UpdateFontPreferencesAsync(value, EditorFontSize);

    partial void OnEditorFontSizeChanged(double value) =>
        _ = UpdateFontPreferencesAsync(EditorFontFamily, value);

    partial void OnResponseViewModeChanged(string value) =>
        ApplyResponseViewMode();

    partial void OnRequestBodyChanged(string value)
    {
        var fmt = StructuredTextEngine.DetectFormat(value);
        RequestHighlighting = fmt == StructuredFormat.Json ? "Json"
            : fmt == StructuredFormat.Xml ? "XML" : "None";
    }

    public async Task InitializeSettingsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await settingsStore.ReadAsync(cancellationToken).ConfigureAwait(true);
            if (!string.IsNullOrEmpty(settings.EditorFontFamily))
                EditorFontFamily = settings.EditorFontFamily;
            if (settings.EditorFontSize >= 8 && settings.EditorFontSize <= 72)
                EditorFontSize = settings.EditorFontSize;
        }
        catch
        {
            // Fall back to default Segoe UI 12 pt (16 px)
        }
    }

    public async Task UpdateFontPreferencesAsync(string family, double size, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await settingsStore.ReadAsync(cancellationToken).ConfigureAwait(false);
            var updated = existing with { EditorFontFamily = family, EditorFontSize = size };
            await settingsStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Settings persistence failure should not disrupt active editing
        }
    }

    [RelayCommand]
    private void FormatRequestBody()
    {
        if (string.IsNullOrWhiteSpace(RequestBody))
        {
            RequestBodyDiagnostic = "";
            return;
        }

        var beautify = StructuredTextEngine.Beautify(RequestBody);
        if (beautify.Success)
        {
            RequestBody = beautify.Text;
            RequestBodyDiagnostic = "";
            RequestHighlighting = beautify.Format == StructuredFormat.Json ? "Json"
                : beautify.Format == StructuredFormat.Xml ? "XML" : "None";
        }
        else
        {
            RequestBodyDiagnostic = beautify.Diagnostic ?? "Cannot format content";
        }
    }

    private void ApplyResponseViewMode()
    {
        if (string.IsNullOrEmpty(RawResponseBody))
        {
            ResponseBody = "";
            ResponseDiagnostic = "";
            return;
        }

        if (string.Equals(ResponseViewMode, "Raw", StringComparison.OrdinalIgnoreCase))
        {
            ResponseBody = RawResponseBody;
            ResponseDiagnostic = "";
            ResponseHighlighting = "None";
            return;
        }

        var beautify = StructuredTextEngine.Beautify(RawResponseBody);
        if (beautify.Success)
        {
            ResponseBody = beautify.Text;
            ResponseDiagnostic = "";
            ResponseHighlighting = beautify.Format == StructuredFormat.Json ? "Json"
                : beautify.Format == StructuredFormat.Xml ? "XML" : "None";
        }
        else
        {
            ResponseBody = RawResponseBody;
            ResponseDiagnostic = beautify.Diagnostic ?? "Parse error";
            ResponseHighlighting = "None";
        }
    }

    [RelayCommand]
    private void NewRequest()
    {
        CurrentRequestId = Guid.NewGuid();
        CurrentRequestPath = null;
        SelectedProjectRequest = null;
        RequestName = "New request";
        RequestDescription = "";
        Method = "GET";
        Url = "http://localhost:5057/echo";
        RequestQuery = "";
        RequestHeaders = "Accept: application/json";
        RequestBody = "";
        BodyMode = "raw";
        BodyFilePath = "";
        AuthType = "none";
        AuthUsername = "";
        AuthPassword = "";
        AuthToken = "";
        AuthApiKeyName = "X-Api-Key";
        AuthApiKeyValue = "";
        AuthApiKeyLocation = "header";
        RawResponseBody = "";
        ResponseBody = "";
        ResponseHeaders = "";
        ResponseCookies = "";
        ResponseDiagnostic = "";
        RequestBodyDiagnostic = "";
        Status = "New request created.";
    }

    [RelayCommand]
    private void CloseRequest()
    {
        CurrentRequestId = null;
        CurrentRequestPath = null;
        SelectedProjectRequest = null;
        RequestName = "";
        RequestDescription = "";
        Method = "GET";
        Url = "";
        RequestQuery = "";
        RequestHeaders = "";
        RequestBody = "";
        RawResponseBody = "";
        ResponseBody = "";
        ResponseHeaders = "";
        ResponseCookies = "";
        ResponseDiagnostic = "";
        RequestBodyDiagnostic = "";
        Status = "Workbench closed.";
    }

    partial void OnSelectedProjectRequestChanged(ProjectAsset? value)
    {
        if (value is not null) _ = LoadRequestAsync(value);
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
            RequestHeaders = string.Join(Environment.NewLine, req.Headers.Select(h => $"{h.Name}: {h.Value}"));
            RequestQuery = string.Join(Environment.NewLine, req.Query.Select(q => $"{q.Name}={q.Value}"));
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
            AuthType = req.Auth?.Type ?? "none";
            AuthUsername = req.Auth?.Username ?? "";
            AuthPassword = req.Auth?.Password ?? "";
            AuthToken = req.Auth?.Token ?? "";
            AuthApiKeyName = req.Auth?.ApiKeyName ?? "X-Api-Key";
            AuthApiKeyValue = req.Auth?.ApiKeyValue ?? "";
            AuthApiKeyLocation = req.Auth?.ApiKeyLocation ?? "header";
            Status = $"Loaded request {target.Path}.";
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
            var result = await Task.Run(() => requests.SendAsync(request, variableContext: varContext, cancellationToken: token), token);
            Status = $"{result.Outcome} · HTTP {result.StatusCode?.ToString() ?? "—"} · {result.Duration.TotalMilliseconds:F0} ms · {result.BytesReceived:N0} bytes";
            RawResponseBody = result.Error ?? result.Preview;
            ApplyResponseViewMode();
            if (result.PreviewTruncated)
            {
                Status += " · preview truncated";
                ResponseDiagnostic = "Response preview truncated at configured limit (5 MiB). Showing raw content.";
            }
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

        var authDef = new RequestAuthDefinition
        {
            Type = AuthType,
            Username = AuthUsername,
            Password = AuthPassword,
            Token = AuthToken,
            ApiKeyName = AuthApiKeyName,
            ApiKeyValue = AuthApiKeyValue,
            ApiKeyLocation = AuthApiKeyLocation
        };

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
            },
            Auth = authDef
        };
    }
}
