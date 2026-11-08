namespace ApiWrangler.Domain;

public enum GrpcStatusCode
{
    OK = 0,
    Cancelled = 1,
    Unknown = 2,
    InvalidArgument = 3,
    DeadlineExceeded = 4,
    NotFound = 5,
    AlreadyExists = 6,
    PermissionDenied = 7,
    ResourceExhausted = 8,
    FailedPrecondition = 9,
    Aborted = 10,
    OutOfRange = 11,
    Unimplemented = 12,
    Internal = 13,
    Unavailable = 14,
    DataLoss = 15,
    Unauthenticated = 16
}

public enum GrpcMethodType
{
    Unary,
    ServerStreaming,
    ClientStreaming,
    BidirectionalStreaming
}

public enum GrpcTranscriptDirection
{
    Inbound,
    Outbound,
    System
}

public enum GrpcTranscriptEventType
{
    Connected,
    MetadataSent,
    MessageSent,
    MessageReceived,
    TrailersReceived,
    Completed,
    Cancelled,
    Error
}

public sealed record GrpcMetadataEntry(string Key, string Value, bool IsBinary = false);

public sealed record GrpcTranscriptEvent(
    DateTimeOffset Timestamp,
    GrpcTranscriptDirection Direction,
    GrpcTranscriptEventType EventType,
    string? Payload = null
);

public sealed record GrpcMethodDefinition(
    string ServiceName,
    string MethodName,
    GrpcMethodType MethodType,
    string InputType,
    string OutputType
)
{
    public string FullPath => $"/{ServiceName}/{MethodName}";
}

public sealed record GrpcServiceDefinition(
    string PackageName,
    string ServiceName,
    IReadOnlyList<GrpcMethodDefinition> Methods
);

public sealed record GrpcExecutionResult(
    GrpcStatusCode StatusCode,
    string? StatusDetail,
    IReadOnlyList<GrpcMetadataEntry> ResponseMetadata,
    IReadOnlyList<GrpcMetadataEntry> ResponseTrailers,
    IReadOnlyList<string> ResponseMessages,
    IReadOnlyList<GrpcTranscriptEvent> Transcript,
    TimeSpan Duration
)
{
    public bool Success => StatusCode == GrpcStatusCode.OK;
}

