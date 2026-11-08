using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ApiWrangler.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApiWrangler.Application;

public sealed class WebhookService : IAsyncDisposable
{
    private readonly List<WebhookListenerDefinition> listeners = new();
    private readonly List<WebhookCapturedEvent> events = new();
    private readonly object syncRoot = new();
    private WebApplication? app;
    private string? currentListeningUrl;

    public string? ListeningUrl => currentListeningUrl;
    public IReadOnlyList<WebhookListenerDefinition> Listeners { get { lock (syncRoot) return listeners.ToList(); } }
    public IReadOnlyList<WebhookCapturedEvent> Events { get { lock (syncRoot) return events.ToList(); } }

    public void AddListener(WebhookListenerDefinition listener)
    {
        lock (syncRoot)
        {
            listeners.Add(listener);
        }
    }

    public void ClearListeners()
    {
        lock (syncRoot)
        {
            listeners.Clear();
        }
    }

    public void ClearEvents()
    {
        lock (syncRoot)
        {
            events.Clear();
        }
    }

    public static bool VerifySignature(
        WebhookSignatureType type,
        byte[] rawBody,
        string? signatureHeaderValue,
        string secret,
        out string? diagnostic)
    {
        diagnostic = null;
        if (type == WebhookSignatureType.None)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(signatureHeaderValue))
        {
            diagnostic = "Missing signature header.";
            return false;
        }

        if (string.IsNullOrEmpty(secret))
        {
            diagnostic = "Missing signing secret.";
            return false;
        }

        try
        {
            byte[] secretBytes = Encoding.UTF8.GetBytes(secret);

            switch (type)
            {
                case WebhookSignatureType.HmacSha256Hex:
                    {
                        // Clean "sha256=" prefix if present
                        string expectedHex = signatureHeaderValue.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
                            ? signatureHeaderValue[7..].Trim()
                            : signatureHeaderValue.Trim();

                        using var hmac = new HMACSHA256(secretBytes);
                        byte[] hash = hmac.ComputeHash(rawBody);
                        string computedHex = Convert.ToHexString(hash).ToLowerInvariant();

                        bool matches = CryptographicOperations.FixedTimeEquals(
                            Encoding.UTF8.GetBytes(computedHex),
                            Encoding.UTF8.GetBytes(expectedHex.ToLowerInvariant()));

                        if (!matches) diagnostic = $"Signature mismatch (expected: {expectedHex}, computed: {computedHex}).";
                        return matches;
                    }

                case WebhookSignatureType.HmacSha256Base64:
                    {
                        using var hmac = new HMACSHA256(secretBytes);
                        byte[] hash = hmac.ComputeHash(rawBody);
                        string computedBase64 = Convert.ToBase64String(hash);

                        bool matches = CryptographicOperations.FixedTimeEquals(
                            Encoding.UTF8.GetBytes(computedBase64),
                            Encoding.UTF8.GetBytes(signatureHeaderValue.Trim()));

                        if (!matches) diagnostic = "Base64 signature mismatch.";
                        return matches;
                    }

                case WebhookSignatureType.HmacSha1Hex:
                    {
                        string expectedHex = signatureHeaderValue.StartsWith("sha1=", StringComparison.OrdinalIgnoreCase)
                            ? signatureHeaderValue[5..].Trim()
                            : signatureHeaderValue.Trim();

                        using var hmac = new HMACSHA1(secretBytes);
                        byte[] hash = hmac.ComputeHash(rawBody);
                        string computedHex = Convert.ToHexString(hash).ToLowerInvariant();

                        bool matches = CryptographicOperations.FixedTimeEquals(
                            Encoding.UTF8.GetBytes(computedHex),
                            Encoding.UTF8.GetBytes(expectedHex.ToLowerInvariant()));

                        if (!matches) diagnostic = $"SHA1 signature mismatch.";
                        return matches;
                    }

                default:
                    diagnostic = $"Unsupported signature type '{type}'.";
                    return false;
            }
        }
        catch (Exception ex)
        {
            diagnostic = $"Signature verification error: {ex.Message}";
            return false;
        }
    }

    public async Task StartAsync(string host = "127.0.0.1", int port = 0, CancellationToken token = default)
    {
        string url = $"http://{host}:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);

        app = builder.Build();

        app.MapFallback(async (HttpContext context) =>
        {
            string path = context.Request.Path.Value ?? "/";
            WebhookListenerDefinition? matchedListener = null;

            lock (syncRoot)
            {
                matchedListener = listeners.FirstOrDefault(l =>
                    path.StartsWith(l.PathPrefix, StringComparison.OrdinalIgnoreCase));
            }

            if (matchedListener is null)
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync("{\"error\": \"No webhook listener registered for path.\"}", context.RequestAborted).ConfigureAwait(false);
                return;
            }

            // Body size check
            if (context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > matchedListener.MaxBodySizeBytes)
            {
                context.Response.StatusCode = 413; // Payload Too Large
                await context.Response.WriteAsync("{\"error\": \"Webhook payload exceeds configured body size limit.\"}", context.RequestAborted).ConfigureAwait(false);
                return;
            }

            // Read raw bytes directly to ensure byte-perfect signature fidelity
            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms, context.RequestAborted).ConfigureAwait(false);
            byte[] rawBytes = ms.ToArray();
            string bodyString = Encoding.UTF8.GetString(rawBytes);

            // Signature verification
            string? sigHeaderValue = null;
            if (!string.IsNullOrEmpty(matchedListener.SignatureHeader))
            {
                if (context.Request.Headers.TryGetValue(matchedListener.SignatureHeader, out var val))
                {
                    sigHeaderValue = val.ToString();
                }
            }

            string secret = "";
            if (!string.IsNullOrEmpty(matchedListener.SecretEnvironmentVariable))
            {
                secret = Environment.GetEnvironmentVariable(matchedListener.SecretEnvironmentVariable) ?? "";
            }

            bool sigVerified = VerifySignature(
                matchedListener.SignatureType,
                rawBytes,
                sigHeaderValue,
                secret,
                out string? sigDiag
            );

            var headersDict = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());

            var captured = new WebhookCapturedEvent(
                Id: Guid.NewGuid().ToString("N"),
                ListenerId: matchedListener.Id,
                ReceivedAt: DateTimeOffset.UtcNow,
                Method: context.Request.Method,
                Path: path,
                QueryString: context.Request.QueryString.Value ?? "",
                Headers: headersDict,
                RawBody: rawBytes,
                BodyString: bodyString,
                SignatureVerified: sigVerified,
                SignatureDiagnostic: sigDiag
            );

            lock (syncRoot)
            {
                events.Add(captured);
            }

            context.Response.StatusCode = matchedListener.ResponseStatusCode;
            context.Response.ContentType = matchedListener.ResponseContentType ?? "application/json";
            if (!string.IsNullOrEmpty(matchedListener.ResponseBody))
            {
                await context.Response.WriteAsync(matchedListener.ResponseBody, context.RequestAborted).ConfigureAwait(false);
            }
        });

        await app.StartAsync(token).ConfigureAwait(false);
        currentListeningUrl = app.Urls.FirstOrDefault();
    }

    public async Task<WebhookReplayResult> ReplayAsync(
        HttpClient client,
        WebhookReplayRequest request,
        CancellationToken token = default)
    {
        WebhookCapturedEvent? sourceEvent;
        WebhookListenerDefinition? listener;

        lock (syncRoot)
        {
            sourceEvent = events.FirstOrDefault(e => e.Id == request.EventId);
            listener = listeners.FirstOrDefault(l => l.Id == sourceEvent?.ListenerId);
        }

        if (sourceEvent is null)
        {
            return new WebhookReplayResult(
                Success: false,
                EventId: request.EventId,
                ReplayEventId: "",
                DestinationUrl: request.DestinationUrl,
                StatusCode: 0,
                Duration: TimeSpan.Zero,
                ErrorMessage: $"Captured event '{request.EventId}' not found."
            );
        }

        // Destination policy check
        if (listener?.AllowedForwardingDestinations is not null && listener.AllowedForwardingDestinations.Count > 0)
        {
            bool isAllowed = listener.AllowedForwardingDestinations.Any(d =>
                request.DestinationUrl.StartsWith(d, StringComparison.OrdinalIgnoreCase));

            if (!isAllowed)
            {
                return new WebhookReplayResult(
                    Success: false,
                    EventId: request.EventId,
                    ReplayEventId: "",
                    DestinationUrl: request.DestinationUrl,
                    StatusCode: 403,
                    Duration: TimeSpan.Zero,
                    ErrorMessage: $"Destination '{request.DestinationUrl}' is forbidden by listener forwarding destination policy."
                );
            }
        }

        byte[] payloadBytes = request.ModifiedBody ?? sourceEvent.RawBody;
        bool isEdit = request.ModifiedBody is not null;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, request.DestinationUrl);
        httpRequest.Content = new ByteArrayContent(payloadBytes);

        // Copy original headers or apply modifications
        var headersToUse = request.ModifiedHeaders ?? sourceEvent.Headers;
        foreach (var (k, v) in headersToUse)
        {
            if (k.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!httpRequest.Headers.TryAddWithoutValidation(k, v))
            {
                httpRequest.Content.Headers.TryAddWithoutValidation(k, v);
            }
        }

        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(httpRequest, token).ConfigureAwait(false);
            sw.Stop();
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new WebhookReplayResult(
                Success: false,
                EventId: request.EventId,
                ReplayEventId: "",
                DestinationUrl: request.DestinationUrl,
                StatusCode: 0,
                Duration: sw.Elapsed,
                ErrorMessage: $"Replay failed: {ex.Message}"
            );
        }

        string replayEventId = Guid.NewGuid().ToString("N");
        var replayLog = new WebhookCapturedEvent(
            Id: replayEventId,
            ListenerId: sourceEvent.ListenerId,
            ReceivedAt: DateTimeOffset.UtcNow,
            Method: "POST",
            Path: request.DestinationUrl,
            QueryString: "",
            Headers: headersToUse,
            RawBody: payloadBytes,
            BodyString: Encoding.UTF8.GetString(payloadBytes),
            SignatureVerified: sourceEvent.SignatureVerified,
            SignatureDiagnostic: sourceEvent.SignatureDiagnostic,
            IsReplay: true,
            ParentEventId: sourceEvent.Id,
            EditNotes: isEdit ? (request.EditNotes ?? "Edited before replay") : "Exact byte replay"
        );

        lock (syncRoot)
        {
            events.Add(replayLog);
        }

        return new WebhookReplayResult(
            Success: response.IsSuccessStatusCode,
            EventId: sourceEvent.Id,
            ReplayEventId: replayEventId,
            DestinationUrl: request.DestinationUrl,
            StatusCode: (int)response.StatusCode,
            Duration: sw.Elapsed
        );
    }

    public async Task StopAsync(CancellationToken token = default)
    {
        if (app is not null)
        {
            await app.StopAsync(token).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            app = null;
            currentListeningUrl = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}

