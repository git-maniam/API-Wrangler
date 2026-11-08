using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiWrangler.Desktop;

public partial class ProtocolExpansionViewModel : ObservableObject
{
    private static readonly HttpClient HttpClientInstance = new();

    // GraphQL
    [ObservableProperty] private string graphQLEndpoint = "http://127.0.0.1:5057/graphql";
    [ObservableProperty] private string graphQLQuery = "query Introspection {\n  __schema {\n    types { name kind }\n  }\n}";
    [ObservableProperty] private string graphQLVariables = "{}";
    [ObservableProperty] private string graphQLResponse = "";
    [ObservableProperty] private string graphQLSchemaSdl = "";
    [ObservableProperty] private string graphQLStatus = "Ready";

    // gRPC
    [ObservableProperty] private string grpcEndpoint = "127.0.0.1:50051";
    [ObservableProperty] private string protoContent = """
        syntax = "proto3";
        package api.sample;

        service SampleService {
          rpc SayHello (HelloRequest) returns (HelloResponse);
          rpc StreamUpdates (UpdateRequest) returns (stream UpdateMessage);
        }
        """;
    [ObservableProperty] private string grpcMethod = "/api.sample.SampleService/SayHello";
    [ObservableProperty] private string grpcPayload = "{\"name\": \"Alice\"}";
    [ObservableProperty] private string grpcTranscript = "";
    [ObservableProperty] private string grpcStatus = "Ready";

    // WebSocket & SSE
    [ObservableProperty] private string streamingEndpoint = "ws://127.0.0.1:8080/ws";
    [ObservableProperty] private string streamingMessage = "{\"type\": \"ping\"}";
    [ObservableProperty] private string streamingTranscript = "";
    [ObservableProperty] private string streamingStatus = "Ready";

    private WebSocketSession? activeWebSocket;
    private SseSession? activeSse;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool isBusy;
    public bool IsIdle => !IsBusy;

    [RelayCommand]
    public async Task ExecuteGraphQLAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        GraphQLStatus = "Executing GraphQL request...";
        try
        {
            JsonObject? varsObj = null;
            if (!string.IsNullOrWhiteSpace(GraphQLVariables))
            {
                try { varsObj = JsonNode.Parse(GraphQLVariables) as JsonObject; } catch { }
            }

            var req = new GraphQLRequestDefinition(
                Query: GraphQLQuery,
                Variables: varsObj
            );

            var result = await GraphQLService.ExecuteAsync(HttpClientInstance, GraphQLEndpoint, req);
            GraphQLResponse = result.RawJson;
            GraphQLStatus = result.Success
                ? $"Success ({result.Duration.TotalMilliseconds:F1} ms, HTTP {result.StatusCode})"
                : $"Failed with {result.Errors.Count} error(s) (HTTP {result.StatusCode})";
        }
        catch (Exception ex)
        {
            GraphQLStatus = $"Error: {ex.Message}";
            GraphQLResponse = ex.ToString();
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void ParseProto()
    {
        try
        {
            var result = ProtoParser.Parse(ProtoContent, "service.proto", Array.Empty<string>());
            if (result.IsValid)
            {
                var methods = result.Services.SelectMany(s => s.Methods).ToList();
                GrpcStatus = $"Parsed {result.Services.Count} service(s), {methods.Count} method(s).";
                if (methods.Count > 0)
                {
                    GrpcMethod = methods[0].FullPath;
                }
            }
            else
            {
                GrpcStatus = $"Proto syntax/import error: {string.Join("; ", result.MissingImports.Concat(result.SyntaxErrors))}";
            }
        }
        catch (Exception ex)
        {
            GrpcStatus = $"Parse error: {ex.Message}";
        }
    }

    [RelayCommand]
    public void SimulateGrpcCall()
    {
        var session = new GrpcStreamingSession();
        session.RecordConnect(GrpcEndpoint, GrpcMethod);
        session.RecordMetadataSent([new GrpcMetadataEntry("user-agent", "api-wrangler/1.0")]);
        session.RecordMessageSent(GrpcPayload);
        session.RecordMetadataReceived([new GrpcMetadataEntry("content-type", "application/grpc")]);
        session.RecordMessageReceived("{\"message\": \"Hello, simulated response!\"}");
        session.RecordTrailersReceived([new GrpcMetadataEntry("grpc-status", "0")]);

        var result = session.Complete(GrpcStatusCode.OK, "Call completed successfully");

        GrpcTranscript = string.Join("\n", result.Transcript.Select(t =>
            $"[{t.Timestamp:HH:mm:ss.fff}] [{t.Direction}] {t.EventType}: {t.Payload}"));

        GrpcStatus = $"Completed ({result.StatusCode}, {result.Duration.TotalMilliseconds:F1} ms)";
    }

    [RelayCommand]
    public async Task ConnectStreamingAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StreamingStatus = $"Connecting to {StreamingEndpoint}...";

        try
        {
            if (StreamingEndpoint.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                StreamingEndpoint.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
            {
                if (activeWebSocket is not null)
                {
                    await activeWebSocket.DisposeAsync();
                }

                activeWebSocket = new WebSocketSession();
                await activeWebSocket.ConnectAsync(new Uri(StreamingEndpoint), timeout: TimeSpan.FromSeconds(5));
                StreamingStatus = $"Connected (WebSocket: {activeWebSocket.State})";
                UpdateStreamingTranscript(activeWebSocket.Transcript);
            }
            else
            {
                if (activeSse is not null)
                {
                    await activeSse.DisposeAsync();
                }

                activeSse = new SseSession();
                await activeSse.ConnectAsync(new Uri(StreamingEndpoint), timeout: TimeSpan.FromSeconds(5));
                StreamingStatus = "Connected to SSE stream";
                UpdateStreamingTranscript(activeSse.Transcript);
            }
        }
        catch (Exception ex)
        {
            StreamingStatus = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task SendWebSocketMessageAsync()
    {
        if (activeWebSocket is null)
        {
            StreamingStatus = "Not connected to a WebSocket session.";
            return;
        }

        try
        {
            await activeWebSocket.SendMessageAsync(StreamingMessage);
            UpdateStreamingTranscript(activeWebSocket.Transcript);
        }
        catch (Exception ex)
        {
            StreamingStatus = $"Send failed: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task DisconnectStreamingAsync()
    {
        if (activeWebSocket is not null)
        {
            await activeWebSocket.DisposeAsync();
            UpdateStreamingTranscript(activeWebSocket.Transcript);
            activeWebSocket = null;
        }

        if (activeSse is not null)
        {
            await activeSse.DisposeAsync();
            UpdateStreamingTranscript(activeSse.Transcript);
            activeSse = null;
        }

        StreamingStatus = "Disconnected";
    }

    private void UpdateStreamingTranscript(IReadOnlyList<StreamingTranscriptEntry> entries)
    {
        StreamingTranscript = string.Join("\n", entries.Select(t =>
            $"[{t.Timestamp:HH:mm:ss.fff}] [{t.Direction}] {t.EventType}: {t.Payload}"));
    }
}

