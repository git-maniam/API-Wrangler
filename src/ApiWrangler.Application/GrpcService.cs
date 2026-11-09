using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed record ProtoImportResolutionResult(
    IReadOnlyList<GrpcServiceDefinition> Services,
    IReadOnlyList<string> ResolvedFiles,
    IReadOnlyList<string> MissingImports,
    IReadOnlyList<string> SyntaxErrors
)
{
    public bool IsValid => MissingImports.Count == 0 && SyntaxErrors.Count == 0;
}

public static class ProtoParser
{
    public static ProtoImportResolutionResult Parse(
        string rootProtoContent,
        string rootFileName,
        IReadOnlyList<string> includeDirectories,
        Func<string, string?>? fileReader = null)
    {
        var services = new List<GrpcServiceDefinition>();
        var resolvedFiles = new List<string> { rootFileName };
        var missingImports = new List<string>();
        var syntaxErrors = new List<string>();
        var processedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootFileName };

        ParseSingleProto(rootProtoContent, rootFileName, services, syntaxErrors);

        // Process imports
        var queue = new Queue<string>(ExtractImports(rootProtoContent));
        while (queue.Count > 0)
        {
            string importPath = queue.Dequeue();
            if (!processedFiles.Add(importPath)) continue;

            string? resolvedContent = null;
            string? foundFullPath = null;

            if (fileReader != null)
            {
                resolvedContent = fileReader(importPath);
            }

            if (resolvedContent == null)
            {
                foreach (var dir in includeDirectories)
                {
                    string candidate = Path.Combine(dir, importPath);
                    if (File.Exists(candidate))
                    {
                        foundFullPath = candidate;
                        resolvedContent = File.ReadAllText(candidate);
                        break;
                    }
                }
            }

            if (resolvedContent != null)
            {
                resolvedFiles.Add(foundFullPath ?? importPath);
                ParseSingleProto(resolvedContent, importPath, services, syntaxErrors);
                foreach (var nestedImport in ExtractImports(resolvedContent))
                {
                    queue.Enqueue(nestedImport);
                }
            }
            else
            {
                missingImports.Add($"Import '{importPath}' not found in include paths: [{string.Join(", ", includeDirectories)}]");
            }
        }

        return new ProtoImportResolutionResult(
            Services: services,
            ResolvedFiles: resolvedFiles,
            MissingImports: missingImports,
            SyntaxErrors: syntaxErrors
        );
    }

    private static IReadOnlyList<string> ExtractImports(string protoContent)
    {
        var imports = new List<string>();
        var matches = Regex.Matches(protoContent, @"import\s+[""']([^""']+)[""'];");
        foreach (Match m in matches)
        {
            if (m.Groups[1].Success)
            {
                imports.Add(m.Groups[1].Value);
            }
        }
        return imports;
    }

    private static void ParseSingleProto(
        string content,
        string fileName,
        List<GrpcServiceDefinition> services,
        List<string> syntaxErrors)
    {
        // Strip comments
        string clean = Regex.Replace(content, @"//[^\r\n]*", "");
        clean = Regex.Replace(clean, @"/\*[\s\S]*?\*/", "");

        string trimmed = clean.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            syntaxErrors.Add($"Empty protobuf content in '{fileName}'.");
            return;
        }

        bool hasProtoConstruct = Regex.IsMatch(trimmed, @"\b(syntax\s*=\s*[""']proto[23][""']|package\s+|service\s+[A-Za-z0-9_]+\s*\{|message\s+[A-Za-z0-9_]+\s*\{|enum\s+[A-Za-z0-9_]+\s*\{|import\s+)");
        if (!hasProtoConstruct)
        {
            syntaxErrors.Add($"Invalid protobuf definition in '{fileName}': no valid syntax, package, service, message, or enum constructs recognized.");
            return;
        }

        string packageName = "";
        var pkgMatch = Regex.Match(clean, @"package\s+([A-Za-z0-9_.]+)\s*;");
        if (pkgMatch.Success)
        {
            packageName = pkgMatch.Groups[1].Value;
        }

        // Match service ServiceName { rpc Method(...) returns (...); }
        var serviceMatches = Regex.Matches(clean, @"service\s+([A-Za-z0-9_]+)\s*\{([^}]+)\}");
        foreach (Match sm in serviceMatches)
        {
            string serviceName = sm.Groups[1].Value;
            string serviceBody = sm.Groups[2].Value;
            var methods = new List<GrpcMethodDefinition>();

            // rpc MethodName ( [stream] InputType ) returns ( [stream] OutputType )
            var rpcMatches = Regex.Matches(serviceBody, @"rpc\s+([A-Za-z0-9_]+)\s*\(\s*(stream\s+)?([A-Za-z0-9_.]+)\s*\)\s*returns\s*\(\s*(stream\s+)?([A-Za-z0-9_.]+)\s*\)");
            foreach (Match rm in rpcMatches)
            {
                string methodName = rm.Groups[1].Value;
                bool clientStream = rm.Groups[2].Success && !string.IsNullOrWhiteSpace(rm.Groups[2].Value);
                string inputType = rm.Groups[3].Value;
                bool serverStream = rm.Groups[4].Success && !string.IsNullOrWhiteSpace(rm.Groups[4].Value);
                string outputType = rm.Groups[5].Value;

                GrpcMethodType methodType = (clientStream, serverStream) switch
                {
                    (false, false) => GrpcMethodType.Unary,
                    (false, true) => GrpcMethodType.ServerStreaming,
                    (true, false) => GrpcMethodType.ClientStreaming,
                    (true, true) => GrpcMethodType.BidirectionalStreaming
                };

                string fullServiceName = string.IsNullOrEmpty(packageName) ? serviceName : $"{packageName}.{serviceName}";
                methods.Add(new GrpcMethodDefinition(fullServiceName, methodName, methodType, inputType, outputType));
            }

            services.Add(new GrpcServiceDefinition(packageName, serviceName, methods));
        }
    }
}

public sealed class GrpcStreamingSession
{
    private readonly List<GrpcTranscriptEvent> transcript = new();
    private readonly List<string> receivedMessages = new();
    private readonly List<GrpcMetadataEntry> responseMetadata = new();
    private readonly List<GrpcMetadataEntry> responseTrailers = new();
    private readonly Stopwatch stopwatch = new();

    public IReadOnlyList<GrpcTranscriptEvent> Transcript => transcript;
    public IReadOnlyList<string> ReceivedMessages => receivedMessages;
    public IReadOnlyList<GrpcMetadataEntry> ResponseMetadata => responseMetadata;
    public IReadOnlyList<GrpcMetadataEntry> ResponseTrailers => responseTrailers;
    public TimeSpan Elapsed => stopwatch.Elapsed;

    public void RecordConnect(string endpoint, string fullMethodPath)
    {
        stopwatch.Restart();
        transcript.Add(new GrpcTranscriptEvent(
            DateTimeOffset.UtcNow,
            GrpcTranscriptDirection.System,
            GrpcTranscriptEventType.Connected,
            $"Connected to {endpoint} for method {fullMethodPath}"
        ));
    }

    public void RecordMetadataSent(IReadOnlyList<GrpcMetadataEntry> metadata)
    {
        string metaStr = string.Join("; ", metadata.Select(m => $"{m.Key}: {m.Value}"));
        transcript.Add(new GrpcTranscriptEvent(
            DateTimeOffset.UtcNow,
            GrpcTranscriptDirection.Outbound,
            GrpcTranscriptEventType.MetadataSent,
            metaStr
        ));
    }

    public void RecordMessageSent(string messageJson)
    {
        transcript.Add(new GrpcTranscriptEvent(
            DateTimeOffset.UtcNow,
            GrpcTranscriptDirection.Outbound,
            GrpcTranscriptEventType.MessageSent,
            messageJson
        ));
    }

    public void RecordMetadataReceived(IReadOnlyList<GrpcMetadataEntry> metadata)
    {
        responseMetadata.AddRange(metadata);
        transcript.Add(new GrpcTranscriptEvent(
            DateTimeOffset.UtcNow,
            GrpcTranscriptDirection.Inbound,
            GrpcTranscriptEventType.MetadataSent,
            string.Join("; ", metadata.Select(m => $"{m.Key}: {m.Value}"))
        ));
    }

    public void RecordMessageReceived(string messageJson)
    {
        receivedMessages.Add(messageJson);
        transcript.Add(new GrpcTranscriptEvent(
            DateTimeOffset.UtcNow,
            GrpcTranscriptDirection.Inbound,
            GrpcTranscriptEventType.MessageReceived,
            messageJson
        ));
    }

    public void RecordTrailersReceived(IReadOnlyList<GrpcMetadataEntry> trailers)
    {
        responseTrailers.AddRange(trailers);
        transcript.Add(new GrpcTranscriptEvent(
            DateTimeOffset.UtcNow,
            GrpcTranscriptDirection.Inbound,
            GrpcTranscriptEventType.TrailersReceived,
            string.Join("; ", trailers.Select(m => $"{m.Key}: {m.Value}"))
        ));
    }

    public GrpcExecutionResult Complete(GrpcStatusCode statusCode, string? statusDetail = null)
    {
        stopwatch.Stop();
        transcript.Add(new GrpcTranscriptEvent(
            DateTimeOffset.UtcNow,
            GrpcTranscriptDirection.System,
            GrpcTranscriptEventType.Completed,
            $"Status: {statusCode} ({(int)statusCode}), Detail: {statusDetail ?? "OK"}"
        ));

        return new GrpcExecutionResult(
            StatusCode: statusCode,
            StatusDetail: statusDetail,
            ResponseMetadata: responseMetadata,
            ResponseTrailers: responseTrailers,
            ResponseMessages: receivedMessages,
            Transcript: transcript,
            Duration: stopwatch.Elapsed
        );
    }

    public GrpcExecutionResult Cancel(string reason = "Call cancelled by client.")
    {
        stopwatch.Stop();
        transcript.Add(new GrpcTranscriptEvent(
            DateTimeOffset.UtcNow,
            GrpcTranscriptDirection.System,
            GrpcTranscriptEventType.Cancelled,
            reason
        ));

        return new GrpcExecutionResult(
            StatusCode: GrpcStatusCode.Cancelled,
            StatusDetail: reason,
            ResponseMetadata: responseMetadata,
            ResponseTrailers: responseTrailers,
            ResponseMessages: receivedMessages,
            Transcript: transcript,
            Duration: stopwatch.Elapsed
        );
    }
}

public static class GrpcFrameCodec
{
    public static byte[] EncodeMessage(byte[] payload, bool compressed = false)
    {
        var frame = new byte[5 + payload.Length];
        frame[0] = (byte)(compressed ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(1, 4), (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(5));
        return frame;
    }

    public static (byte[] Payload, bool Compressed) DecodeMessage(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 5) throw new ArgumentException("gRPC frame header must be at least 5 bytes.", nameof(frame));
        bool compressed = frame[0] == 1;
        uint length = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(1, 4));
        if (frame.Length < 5 + length) throw new ArgumentException("Incomplete gRPC frame data.", nameof(frame));
        return (frame.Slice(5, (int)length).ToArray(), compressed);
    }
}

