using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using ApiWrangler.Application;
using ApiWrangler.Domain;

namespace ApiWrangler.Execution;

public sealed class HttpTransport : IHttpTransport, IDisposable
{
    private readonly HttpClient client;
    private readonly string artifactDirectory;

    public HttpTransport(string artifactDirectory) : this(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }, artifactDirectory)
    { }

    public HttpTransport(HttpMessageHandler handler, string artifactDirectory)
    {
        client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        this.artifactDirectory = Path.GetFullPath(artifactDirectory);
    }

    public async Task<HttpExecutionResult> SendAsync(HttpRequestDefinition definition, ExecutionLimits limits,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(definition.TimeoutMs);
        var token = deadline.Token;
        string? bodyPath = null;
        int? status = null;
        string? reason = null;
        List<HeaderEntry> headers = [];
        long received = 0;
        using var preview = new MemoryStream();
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(definition.Method), definition.Url);
            if (definition.Body is not null)
            {
                request.Content = new StringContent(definition.Body, Encoding.UTF8);
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(definition.ContentType);
            }
            foreach (var header in definition.Headers.Where(h => h.Enabled))
            {
                if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value))
                {
                    request.Content ??= new ByteArrayContent([]);
                    if (!request.Content.Headers.TryAddWithoutValidation(header.Name, header.Value))
                        throw new ArgumentException("A header cannot be applied to this request.");
                }
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            reason = response.ReasonPhrase;
            headers = response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => new HeaderEntry(h.Key, v))).ToList();
            Directory.CreateDirectory(artifactDirectory);
            bodyPath = Path.Combine(artifactDirectory, $"{Guid.NewGuid():N}.body");
            await using (var output = new FileStream(bodyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            {
                var buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    if (received + count > limits.MaxResponseBytes)
                        throw new ResponseLimitException();
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    var previewCount = Math.Min(count, limits.PreviewBytes - (int)preview.Length);
                    if (previewCount > 0) preview.Write(buffer, 0, previewCount);
                    received += count;
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            return Result(ExecutionOutcome.Passed, null);
        }
        catch (OperationCanceledException)
        {
            RemovePartial();
            return Result(cancellationToken.IsCancellationRequested ? ExecutionOutcome.Cancelled : ExecutionOutcome.TimedOut,
                cancellationToken.IsCancellationRequested ? "Request cancelled." : "Request deadline exceeded.");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or ArgumentException or FormatException or UnauthorizedAccessException)
        {
            RemovePartial();
            // Do not include exception text: URLs, credentials, and server data may be embedded in it.
            return Result(ExecutionOutcome.Error, exception is ResponseLimitException
                ? "Response exceeded the configured saved-body limit. Partial body removed."
                : "Request failed. Check the URL, headers, connectivity, TLS configuration, and artifact storage.");
        }

        HttpExecutionResult Result(ExecutionOutcome outcome, string? error) => new(outcome, status, reason, headers,
            Encoding.UTF8.GetString(preview.ToArray()), received, received > preview.Length, bodyPath, watch.Elapsed, error);

        void RemovePartial()
        {
            if (bodyPath is not null)
            {
                File.Delete(bodyPath);
                bodyPath = null;
            }
        }
    }

    public void Dispose() => client.Dispose();
    private sealed class ResponseLimitException : IOException;
}
