using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class GraphQLAndGrpcTests
{
    [Fact]
    public void GraphQL_SdlParser_ExtractsTypesFieldsAndArguments()
    {
        string sdl = """
        schema {
          query: RootQuery
          mutation: RootMutation
        }

        type RootQuery {
          user(id: ID!): User
          searchUsers(query: String!, limit: Int = 10): [User!]!
        }

        type RootMutation {
          createUser(name: String!, email: String!): User!
        }

        type User {
          id: ID!
          name: String!
          email: String!
          role: String
        }
        """;

        var schema = GraphQLService.ParseSdl(sdl);

        Assert.Equal("RootQuery", schema.QueryType);
        Assert.Equal("RootMutation", schema.MutationType);

        var queryType = Assert.Single(schema.Types, t => t.Name == "RootQuery");
        Assert.NotNull(queryType.Fields);
        Assert.Equal(2, queryType.Fields!.Count);

        var userField = queryType.Fields.Single(f => f.Name == "user");
        Assert.Equal("User", userField.Type);
        Assert.NotNull(userField.Arguments);
        var idArg = Assert.Single(userField.Arguments);
        Assert.Equal("id", idArg.Name);
        Assert.Equal("ID!", idArg.Type);
        Assert.True(idArg.IsRequired);
    }

    [Fact]
    public void GraphQL_CompletionAndValidation_DetectsFieldsAndErrors()
    {
        string sdl = """
        type Query {
          hero(episode: String): Character
          reviews(limit: Int): [Review]
        }

        type Character {
          id: ID!
          name: String!
          friends: [Character]
        }
        """;

        var schema = GraphQLService.ParseSdl(sdl);

        // Completion suggestions
        var suggestions = GraphQLService.GetCompletionSuggestions(schema, "Character");
        Assert.Contains("friends", suggestions);
        Assert.Contains("id", suggestions);
        Assert.Contains("name", suggestions);

        var filtered = GraphQLService.GetCompletionSuggestions(schema, "Character", "na");
        Assert.Single(filtered);
        Assert.Equal("name", filtered[0]);

        // Validation: valid query
        string validQuery = """
        query GetHero {
          hero(episode: "NEWHOPE") {
            name
          }
        }
        """;
        var validDiags = GraphQLService.ValidateQuery(schema, validQuery);
        Assert.Empty(validDiags);

        // Validation: unknown field
        string invalidQuery = """
        query BadQuery {
          nonExistentField {
            foo
          }
        }
        """;
        var invalidDiags = GraphQLService.ValidateQuery(schema, invalidQuery);
        Assert.NotEmpty(invalidDiags);
        Assert.Contains("nonExistentField", invalidDiags[0]);
    }

    [Fact]
    public async Task GraphQL_Execution_ExecutesQueryAndHandlesErrors()
    {
        // Spin up local lightweight GraphQL test server
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.MapPost("/graphql", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            string body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            string query = doc.RootElement.GetProperty("query").GetString() ?? "";

            if (query.Contains("errorQuery"))
            {
                return Results.Json(new
                {
                    errors = new[]
                    {
                        new
                        {
                            message = "Entity not found",
                            path = new[] { "user" },
                            locations = new[] { new { line = 2, column = 3 } },
                            extensions = new { code = "NOT_FOUND" }
                        }
                    }
                });
            }

            return Results.Json(new
            {
                data = new
                {
                    user = new { id = "u-123", name = "Alice Smith" }
                }
            });
        });

        await app.StartAsync();

        try
        {
            string url = app.Urls.First() + "/graphql";
            using var httpClient = new HttpClient();

            // 1. Successful Query
            var successReq = new GraphQLRequestDefinition(
                Query: "query GetUser { user { id name } }",
                OperationName: "GetUser"
            );
            var successResult = await GraphQLService.ExecuteAsync(httpClient, url, successReq);

            Assert.True(successResult.Success);
            Assert.False(successResult.HasErrors);
            Assert.NotNull(successResult.Data);
            Assert.Equal("Alice Smith", successResult.Data!.Value.GetProperty("user").GetProperty("name").GetString());

            // 2. Query with Errors
            var errorReq = new GraphQLRequestDefinition(Query: "query errorQuery { user { id } }");
            var errorResult = await GraphQLService.ExecuteAsync(httpClient, url, errorReq);

            Assert.False(successResult == errorResult);
            Assert.True(errorResult.HasErrors);
            Assert.Single(errorResult.Errors);
            Assert.Equal("Entity not found", errorResult.Errors[0].Message);
            Assert.Equal("NOT_FOUND", errorResult.Errors[0].Code);
            Assert.Equal("user", errorResult.Errors[0].Path[0]);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void Grpc_ProtoParser_ParsesServicesMethodsAndStreamingTypes()
    {
        string proto = """
        syntax = "proto3";
        package api.orders;

        service OrderService {
          rpc CreateOrder (CreateOrderRequest) returns (OrderResponse);
          rpc StreamOrderStatus (OrderStatusRequest) returns (stream StatusUpdate);
          rpc UploadOrderItems (stream OrderItem) returns (SummaryResponse);
          rpc ChatChannel (stream ChatMessage) returns (stream ChatMessage);
        }
        """;

        var result = ProtoParser.Parse(proto, "orders.proto", Array.Empty<string>());

        Assert.True(result.IsValid);
        var service = Assert.Single(result.Services);
        Assert.Equal("api.orders", service.PackageName);
        Assert.Equal("OrderService", service.ServiceName);
        Assert.Equal(4, service.Methods.Count);

        var unary = service.Methods.Single(m => m.MethodName == "CreateOrder");
        Assert.Equal(GrpcMethodType.Unary, unary.MethodType);
        Assert.Equal("/api.orders.OrderService/CreateOrder", unary.FullPath);

        var serverStream = service.Methods.Single(m => m.MethodName == "StreamOrderStatus");
        Assert.Equal(GrpcMethodType.ServerStreaming, serverStream.MethodType);

        var clientStream = service.Methods.Single(m => m.MethodName == "UploadOrderItems");
        Assert.Equal(GrpcMethodType.ClientStreaming, clientStream.MethodType);

        var bidiStream = service.Methods.Single(m => m.MethodName == "ChatChannel");
        Assert.Equal(GrpcMethodType.BidirectionalStreaming, bidiStream.MethodType);
    }

    [Fact]
    public void Grpc_ProtoParser_MissingImports_ProducesActionableDiagnostics()
    {
        string rootProto = """
        syntax = "proto3";
        import "common/types.proto";
        import "missing/vendor.proto";

        service SampleService {
          rpc Test (Request) returns (Response);
        }
        """;

        var virtualFiles = new Dictionary<string, string>
        {
            ["common/types.proto"] = "syntax = \"proto3\"; package common;"
        };

        var result = ProtoParser.Parse(
            rootProto,
            "sample.proto",
            ["/fake/include/path"],
            fileReader: path => virtualFiles.TryGetValue(path, out var c) ? c : null
        );

        Assert.False(result.IsValid);
        Assert.Contains("sample.proto", result.ResolvedFiles);
        Assert.Contains("common/types.proto", result.ResolvedFiles);

        var missing = Assert.Single(result.MissingImports);
        Assert.Contains("missing/vendor.proto", missing);
        Assert.Contains("/fake/include/path", missing);
    }

    [Fact]
    public void Grpc_FrameCodec_EncodesAndDecodesFiveByteFraming()
    {
        byte[] payload = "{\"greeting\":\"hello world\"}"u8.ToArray();

        byte[] frame = GrpcFrameCodec.EncodeMessage(payload, compressed: false);
        Assert.Equal(5 + payload.Length, frame.Length);
        Assert.Equal(0, frame[0]); // Uncompressed

        var (decodedPayload, compressed) = GrpcFrameCodec.DecodeMessage(frame);
        Assert.False(compressed);
        Assert.Equal(payload, decodedPayload);
    }

    [Fact]
    public void Grpc_StreamingSession_BuildsExplicitTranscriptWithTrailersAndStatus()
    {
        var session = new GrpcStreamingSession();

        session.RecordConnect("127.0.0.1:50051", "/api.orders.OrderService/StreamOrderStatus");
        session.RecordMetadataSent([new GrpcMetadataEntry("authorization", "Bearer token-123")]);
        session.RecordMessageSent("{\"orderId\": 9876}");
        session.RecordMetadataReceived([new GrpcMetadataEntry("content-type", "application/grpc")]);
        session.RecordMessageReceived("{\"status\": \"QUEUED\"}");
        session.RecordMessageReceived("{\"status\": \"PROCESSING\"}");
        session.RecordMessageReceived("{\"status\": \"SHIPPED\"}");
        session.RecordTrailersReceived([
            new GrpcMetadataEntry("grpc-status", "0"),
            new GrpcMetadataEntry("grpc-message", "Stream complete")
        ]);

        var result = session.Complete(GrpcStatusCode.OK, "Completed successfully");

        Assert.True(result.Success);
        Assert.Equal(GrpcStatusCode.OK, result.StatusCode);
        Assert.Equal(3, result.ResponseMessages.Count);
        Assert.Equal(2, result.ResponseTrailers.Count);

        // Verify transcript retains chronological semantics
        Assert.True(result.Transcript.Count >= 7);
        Assert.Equal(GrpcTranscriptEventType.Connected, result.Transcript[0].EventType);
        Assert.Equal(GrpcTranscriptEventType.MetadataSent, result.Transcript[1].EventType);
        Assert.Equal(GrpcTranscriptEventType.MessageSent, result.Transcript[2].EventType);
        Assert.Contains(result.Transcript, e => e.EventType == GrpcTranscriptEventType.MessageReceived && e.Payload!.Contains("SHIPPED"));
        Assert.Equal(GrpcTranscriptEventType.Completed, result.Transcript[^1].EventType);
    }

    [Fact]
    public void Grpc_StreamingSession_Cancellation_RecordsCancelledEvent()
    {
        var session = new GrpcStreamingSession();
        session.RecordConnect("127.0.0.1:50051", "/api.chat.ChatService/ChatChannel");
        session.RecordMessageSent("{\"msg\": \"ping\"}");

        var result = session.Cancel("Deadline exceeded by client timeout.");

        Assert.False(result.Success);
        Assert.Equal(GrpcStatusCode.Cancelled, result.StatusCode);
        Assert.Equal("Deadline exceeded by client timeout.", result.StatusDetail);
        Assert.Equal(GrpcTranscriptEventType.Cancelled, result.Transcript[^1].EventType);
    }
}

