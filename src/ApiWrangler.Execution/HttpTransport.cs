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
    private readonly HttpClient insecureClient;
    private readonly string artifactDirectory;

    public HttpTransport(string artifactDirectory) : this(
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        },
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = delegate { return true; }
            }
        },
        artifactDirectory)
    { }

    public HttpTransport(HttpMessageHandler handler, string artifactDirectory)
        : this(handler, handler, artifactDirectory)
    { }

    public HttpTransport(HttpMessageHandler standardHandler, HttpMessageHandler insecureHandler, string artifactDirectory)
    {
        client = new HttpClient(standardHandler) { Timeout = Timeout.InfiniteTimeSpan };
        insecureClient = new HttpClient(insecureHandler) { Timeout = Timeout.InfiniteTimeSpan };
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
        List<CookieEntry> cookies = [];
        long received = 0;
        using var preview = new MemoryStream();
        string currentUrl = BuildUrl(definition.Url, definition.Query);
        string currentMethod = definition.Method;
        var currentHeaders = definition.Headers.Where(h => h.Enabled).ToList();
        int redirectCount = 0;

        HttpClient? customClient = null;
        SocketsHttpHandler? customHandler = null;
        HttpClient activeClient;
        var auth = definition.Auth;

        if (auth is not null && string.Equals(auth.Type, "windows", StringComparison.OrdinalIgnoreCase))
        {
            customHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(1),
                Credentials = auth.UseDefaultWindowsCredentials
                    ? CredentialCache.DefaultNetworkCredentials
                    : new NetworkCredential(auth.WindowsUsername, auth.WindowsPassword, auth.WindowsDomain)
            };
            if (definition.TransportOptions.DangerousIgnoreTlsErrors)
            {
                customHandler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = delegate { return true; }
                };
            }
            customClient = new HttpClient(customHandler) { Timeout = Timeout.InfiniteTimeSpan };
            activeClient = customClient;
        }
        else if (auth is not null && string.Equals(auth.Type, "certificate", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(auth.ClientCertificatePath))
        {
            customHandler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(1)
            };
            var cert = string.IsNullOrEmpty(auth.ClientCertificatePassword)
                ? (auth.ClientCertificatePath.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || auth.ClientCertificatePath.EndsWith(".p12", StringComparison.OrdinalIgnoreCase)
                    ? System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(auth.ClientCertificatePath, string.Empty)
                    : System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificateFromFile(auth.ClientCertificatePath))
                : System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(auth.ClientCertificatePath, auth.ClientCertificatePassword);

            customHandler.SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                ClientCertificates = [cert],
                RemoteCertificateValidationCallback = definition.TransportOptions.DangerousIgnoreTlsErrors ? delegate { return true; } : null
            };
            customClient = new HttpClient(customHandler) { Timeout = Timeout.InfiniteTimeSpan };
            activeClient = customClient;
        }
        else
        {
            activeClient = definition.TransportOptions.DangerousIgnoreTlsErrors ? insecureClient : client;
        }

        try
        {
            HttpResponseMessage? response = null;
            while (true)
            {
                using var request = new HttpRequestMessage(new HttpMethod(currentMethod), currentUrl);
                if (definition.TransportOptions.PreferredHttpVersion is not null)
                {
                    request.Version = definition.TransportOptions.PreferredHttpVersion;
                }

                HttpContent? content = null;
                if (!HttpMethods.IsGetOrHead(currentMethod))
                {
                    content = CreateContent(definition);
                    request.Content = content;
                }

                foreach (var header in currentHeaders)
                {
                    if (!request.Headers.TryAddWithoutValidation(header.Name, header.Value))
                    {
                        request.Content ??= new ByteArrayContent([]);
                        if (!request.Content.Headers.TryAddWithoutValidation(header.Name, header.Value))
                            throw new ArgumentException("A header cannot be applied to this request.");
                    }
                }

                response = await activeClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                status = (int)response.StatusCode;
                reason = response.ReasonPhrase;

                if (definition.TransportOptions.FollowRedirects && IsRedirect(status.Value) && redirectCount < definition.TransportOptions.MaxRedirects)
                {
                    var location = response.Headers.Location;
                    if (location is not null)
                    {
                        var currentUri = new Uri(currentUrl);
                        var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
                        var isCrossOrigin = !string.Equals(currentUri.Scheme, nextUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                                            !string.Equals(currentUri.Host, nextUri.Host, StringComparison.OrdinalIgnoreCase) ||
                                            currentUri.Port != nextUri.Port;

                        if (isCrossOrigin && definition.TransportOptions.StripAuthOnCrossOriginRedirect)
                        {
                            currentHeaders = currentHeaders
                                .Where(h => !string.Equals(h.Name, "Authorization", StringComparison.OrdinalIgnoreCase) &&
                                            !string.Equals(h.Name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
                                .ToList();
                        }

                        if (status.Value == 303 || ((status.Value == 301 || status.Value == 302) && string.Equals(currentMethod, "POST", StringComparison.OrdinalIgnoreCase)))
                        {
                            currentMethod = "GET";
                        }

                        currentUrl = nextUri.ToString();
                        redirectCount++;
                        response.Dispose();
                        continue;
                    }
                }

                break;
            }

            headers = response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(v => new HeaderEntry(h.Key, v))).ToList();

            cookies = ExtractCookies(response);

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
            response.Dispose();
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
        finally
        {
            customClient?.Dispose();
            customHandler?.Dispose();
        }

        HttpExecutionResult Result(ExecutionOutcome outcome, string? error) => new(outcome, status, reason, headers,
            Encoding.UTF8.GetString(preview.ToArray()), received, received > preview.Length, bodyPath, watch.Elapsed, error,
            cookies, currentUrl, redirectCount);

        void RemovePartial()
        {
            if (bodyPath is not null)
            {
                File.Delete(bodyPath);
                bodyPath = null;
            }
        }
    }

    private static bool IsRedirect(int statusCode) =>
        statusCode is 301 or 302 or 303 or 307 or 308;

    public static string BuildUrl(string baseUrl, IReadOnlyList<QueryParamEntry> query)
    {
        var enabledQuery = query.Where(q => q.Enabled && !string.IsNullOrWhiteSpace(q.Name)).ToList();
        if (enabledQuery.Count == 0) return baseUrl;

        var uriBuilder = new UriBuilder(baseUrl);
        var querySegments = new List<string>();

        if (!string.IsNullOrEmpty(uriBuilder.Query))
        {
            var raw = uriBuilder.Query.TrimStart('?');
            if (!string.IsNullOrEmpty(raw)) querySegments.Add(raw);
        }

        foreach (var param in enabledQuery)
        {
            querySegments.Add($"{Uri.EscapeDataString(param.Name)}={Uri.EscapeDataString(param.Value)}");
        }

        uriBuilder.Query = string.Join("&", querySegments);
        return uriBuilder.Uri.ToString();
    }

    private static HttpContent? CreateContent(HttpRequestDefinition definition)
    {
        if (definition.BodyDefinition is not null)
        {
            var mode = definition.BodyDefinition.Mode.ToLowerInvariant();
            switch (mode)
            {
                case "none":
                    return null;
                case "urlencoded":
                    if (definition.BodyDefinition.FormData.Count > 0)
                    {
                        var pairs = definition.BodyDefinition.FormData
                            .Where(f => f.Enabled && !string.IsNullOrEmpty(f.Name))
                            .Select(f => new KeyValuePair<string, string>(f.Name, f.Value));
                        return new FormUrlEncodedContent(pairs);
                    }
                    if (!string.IsNullOrEmpty(definition.BodyDefinition.Text))
                    {
                        return new StringContent(definition.BodyDefinition.Text, Encoding.UTF8, "application/x-www-form-urlencoded");
                    }
                    return null;
                case "form-data":
                    var multipart = new MultipartFormDataContent();
                    foreach (var item in definition.BodyDefinition.FormData.Where(f => f.Enabled && !string.IsNullOrEmpty(f.Name)))
                    {
                        if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                        {
                            var stream = File.OpenRead(item.FilePath);
                            var streamContent = new StreamContent(stream);
                            if (!string.IsNullOrEmpty(item.ContentType))
                                streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(item.ContentType);
                            multipart.Add(streamContent, item.Name, Path.GetFileName(item.FilePath));
                        }
                        else
                        {
                            multipart.Add(new StringContent(item.Value), item.Name);
                        }
                    }
                    return multipart;
                case "file":
                    if (!string.IsNullOrEmpty(definition.BodyDefinition.FilePath) && File.Exists(definition.BodyDefinition.FilePath))
                    {
                        var stream = File.OpenRead(definition.BodyDefinition.FilePath);
                        var fileContent = new StreamContent(stream);
                        if (!string.IsNullOrEmpty(definition.ContentType))
                            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(definition.ContentType);
                        return fileContent;
                    }
                    return null;
                case "raw":
                default:
                    if (!string.IsNullOrEmpty(definition.BodyDefinition.Text))
                    {
                        var rawContent = new StringContent(definition.BodyDefinition.Text, Encoding.UTF8);
                        if (!string.IsNullOrEmpty(definition.ContentType))
                            rawContent.Headers.ContentType = MediaTypeHeaderValue.Parse(definition.ContentType);
                        return rawContent;
                    }
                    return null;
            }
        }

        if (definition.Body is not null)
        {
            var content = new StringContent(definition.Body, Encoding.UTF8);
            if (!string.IsNullOrEmpty(definition.ContentType))
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(definition.ContentType);
            return content;
        }

        return null;
    }

    private static List<CookieEntry> ExtractCookies(HttpResponseMessage response)
    {
        var cookies = new List<CookieEntry>();
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookieValues))
        {
            foreach (var raw in setCookieValues)
            {
                var parts = raw.Split(';').Select(p => p.Trim()).ToArray();
                if (parts.Length == 0) continue;
                var eqIdx = parts[0].IndexOf('=');
                if (eqIdx <= 0) continue;
                var name = parts[0][..eqIdx].Trim();
                var value = parts[0][(eqIdx + 1)..].Trim();
                string? domain = null;
                string? path = null;
                DateTimeOffset? expires = null;
                bool httpOnly = false;
                bool secure = false;

                for (var i = 1; i < parts.Length; i++)
                {
                    var part = parts[i];
                    var pEq = part.IndexOf('=');
                    var pKey = (pEq > 0 ? part[..pEq] : part).Trim();
                    var pVal = (pEq > 0 ? part[(pEq + 1)..] : "").Trim();

                    if (string.Equals(pKey, "domain", StringComparison.OrdinalIgnoreCase)) domain = pVal;
                    else if (string.Equals(pKey, "path", StringComparison.OrdinalIgnoreCase)) path = pVal;
                    else if (string.Equals(pKey, "expires", StringComparison.OrdinalIgnoreCase) && DateTimeOffset.TryParse(pVal, out var dt)) expires = dt;
                    else if (string.Equals(pKey, "httponly", StringComparison.OrdinalIgnoreCase)) httpOnly = true;
                    else if (string.Equals(pKey, "secure", StringComparison.OrdinalIgnoreCase)) secure = true;
                }

                cookies.Add(new CookieEntry(name, value, domain, path, expires, httpOnly, secure));
            }
        }
        return cookies;
    }

    private static class HttpMethods
    {
        public static bool IsGetOrHead(string method) =>
            string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        client.Dispose();
        insecureClient.Dispose();
    }

    private sealed class ResponseLimitException : IOException;
}
