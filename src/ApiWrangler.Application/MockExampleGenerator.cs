using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public static class MockExampleGenerator
{
    public static MockRule GenerateFromOpenApi(string path, string method, string operationId, JsonElement schemaOrExample, int statusCode = 200)
    {
        string bodyJson = "{}";
        if (schemaOrExample.ValueKind == JsonValueKind.Object || schemaOrExample.ValueKind == JsonValueKind.Array)
        {
            bodyJson = schemaOrExample.GetRawText();
        }

        return new MockRule(
            Id: Guid.NewGuid().ToString("N"),
            Name: $"OpenAPI: {operationId} ({method} {path})",
            Method: method.ToUpperInvariant(),
            PathPattern: path,
            Priority: 10,
            ResponseStatusCode: statusCode,
            ResponseHeaders: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            ResponseBody: bodyJson
        );
    }

    public static MockRule GenerateFromSavedResponse(string name, string method, string path, int statusCode, IReadOnlyDictionary<string, string>? headers, string? body)
    {
        return new MockRule(
            Id: Guid.NewGuid().ToString("N"),
            Name: $"Saved: {name}",
            Method: method.ToUpperInvariant(),
            PathPattern: path,
            Priority: 20,
            ResponseStatusCode: statusCode,
            ResponseHeaders: headers,
            ResponseBody: body
        );
    }
}

