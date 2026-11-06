using ApiWrangler.Application;
using ApiWrangler.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiWrangler.Desktop;

public partial class MainViewModel(HttpRequestService requests) : ObservableObject
{
    [ObservableProperty] private string method = "GET";
    [ObservableProperty] private string url = "http://localhost:5057/echo";
    [ObservableProperty] private string requestBody = "";
    [ObservableProperty] private string requestHeaders = "Accept: application/json";
    [ObservableProperty] private string responseBody = "Start the local fixture server, then send a request.";
    [ObservableProperty] private string responseHeaders = "";
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private string artifactPath = "";
    public PagedDataset Dataset { get; } = new();
    public string[] Methods { get; } = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task SendAsync(CancellationToken token)
    {
        try
        {
            var headers = RequestHeaders.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    var colon = line.IndexOf(':');
                    if (colon < 1) throw new ArgumentException("Use one Name: value header per line.");
                    return new HeaderEntry(line[..colon].Trim(), line[(colon + 1)..].Trim());
                }).ToArray();
            var request = new HttpRequestDefinition
            {
                Method = Method,
                Url = Url,
                Headers = headers,
                Body = string.IsNullOrEmpty(RequestBody) ? null : RequestBody
            };
            Status = "Sending…";
            ArtifactPath = "";
            // Even validation and synchronous transport setup stay off the dispatcher.
            var result = await Task.Run(() => requests.SendAsync(request, cancellationToken: token), token);
            Status = $"{result.Outcome} · HTTP {result.StatusCode?.ToString() ?? "—"} · {result.Duration.TotalMilliseconds:F0} ms · {result.BytesReceived:N0} bytes";
            ResponseBody = result.Error ?? result.Preview;
            if (result.PreviewTruncated) Status += " · preview truncated";
            ResponseHeaders = string.Join(Environment.NewLine, result.Headers.Select(h => $"{h.Name}: {h.Value}"));
            ArtifactPath = result.BodyPath ?? "";
        }
        catch (ArgumentException exception) { Status = exception.Message; }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception) { Status = "Request could not complete. Check local artifact storage and request settings."; }
    }
}
