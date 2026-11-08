namespace ApiWrangler.Domain;

public sealed record WebhookListenerDefinition(
    string Id,
    string Name,
    string PathPrefix, // e.g. "/webhooks/stripe"
    WebhookSignatureType SignatureType = WebhookSignatureType.None,
    string? SignatureHeader = null,
    string? SecretEnvironmentVariable = null,
    int ResponseStatusCode = 200,
    string? ResponseContentType = "application/json",
    string? ResponseBody = "{\"status\": \"received\"}",
    IReadOnlyList<string>? AllowedForwardingDestinations = null,
    long MaxBodySizeBytes = 1024 * 1024, // 1 MB
    int RateLimitPerMinute = 120
);

public enum WebhookSignatureType
{
    None,
    HmacSha256Hex,      // GitHub (sha256=...), Slack, etc.
    HmacSha256Base64,   // Stripe, generic
    HmacSha1Hex         // Legacy GitHub (sha1=...)
}

public sealed record WebhookCapturedEvent(
    string Id,
    string ListenerId,
    DateTimeOffset ReceivedAt,
    string Method,
    string Path,
    string QueryString,
    IReadOnlyDictionary<string, string> Headers,
    byte[] RawBody,
    string BodyString,
    bool SignatureVerified,
    string? SignatureDiagnostic,
    bool IsReplay = false,
    string? ParentEventId = null,
    string? EditNotes = null
);

public sealed record WebhookReplayRequest(
    string EventId,
    string DestinationUrl,
    byte[]? ModifiedBody = null,
    IReadOnlyDictionary<string, string>? ModifiedHeaders = null,
    string? EditNotes = null
);

public sealed record WebhookReplayResult(
    bool Success,
    string EventId,
    string ReplayEventId,
    string DestinationUrl,
    int StatusCode,
    TimeSpan Duration,
    string? ErrorMessage = null
);

