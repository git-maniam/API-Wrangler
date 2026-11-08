namespace ApiWrangler.Domain;

public enum StreamingProtocol
{
    WebSocket,
    Sse
}

public enum StreamDirection
{
    Inbound,
    Outbound,
    System
}

public enum StreamEventType
{
    Connecting,
    Connected,
    MessageSent,
    MessageReceived,
    PingSent,
    PongReceived,
    Reconnecting,
    Reconnected,
    Disconnected,
    Cancelled,
    Error
}

public sealed record StreamingTranscriptEntry(
    DateTimeOffset Timestamp,
    StreamDirection Direction,
    StreamEventType EventType,
    string? Payload = null,
    long SizeBytes = 0
);

public sealed record StreamingReconnectionPolicy(
    bool Enabled = true,
    int MaxAttempts = 3,
    TimeSpan InitialDelay = default,
    double BackoffMultiplier = 1.5
)
{
    public static readonly StreamingReconnectionPolicy Default = new(
        Enabled: true,
        MaxAttempts: 3,
        InitialDelay: TimeSpan.FromMilliseconds(500),
        BackoffMultiplier: 1.5
    );

    public static readonly StreamingReconnectionPolicy Disabled = new(
        Enabled: false,
        MaxAttempts: 0,
        InitialDelay: TimeSpan.Zero,
        BackoffMultiplier: 1.0
    );
}

public sealed record StreamingLimits(
    int MaxMessages = 1000,
    long MaxBytes = 10 * 1024 * 1024
);

public sealed record SseEventEntry(
    string? Id,
    string EventName,
    string Data,
    int? RetryMs = null
);

