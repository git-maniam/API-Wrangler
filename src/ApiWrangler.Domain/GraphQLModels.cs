using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiWrangler.Domain;

public enum GraphQLOperationType
{
    Query,
    Mutation,
    Subscription
}

public sealed record GraphQLSourceLocation(int Line, int Column);

public sealed record GraphQLErrorEntry(
    string Message,
    IReadOnlyList<string> Path,
    IReadOnlyList<GraphQLSourceLocation> Locations,
    string? Code = null,
    IReadOnlyDictionary<string, JsonElement>? Extensions = null
);

public sealed record GraphQLArgumentDefinition(
    string Name,
    string Type,
    string? DefaultValue = null,
    bool IsRequired = false
);

public sealed record GraphQLFieldDefinition(
    string Name,
    string Type,
    string? Description = null,
    IReadOnlyList<GraphQLArgumentDefinition>? Arguments = null
);

public sealed record GraphQLTypeDefinition(
    string Name,
    string Kind, // OBJECT, SCALAR, INPUT_OBJECT, ENUM, INTERFACE, UNION
    string? Description = null,
    IReadOnlyList<GraphQLFieldDefinition>? Fields = null
);

public sealed record GraphQLSchemaDefinition(
    IReadOnlyList<GraphQLTypeDefinition> Types,
    string QueryType = "Query",
    string? MutationType = "Mutation",
    string? SubscriptionType = "Subscription"
);

public sealed record GraphQLRequestDefinition(
    string Query,
    string? OperationName = null,
    JsonObject? Variables = null
);

public sealed record GraphQLExecutionResult(
    int StatusCode,
    JsonElement? Data,
    IReadOnlyList<GraphQLErrorEntry> Errors,
    string RawJson,
    TimeSpan Duration
)
{
    public bool HasErrors => Errors.Count > 0;
    public bool Success => StatusCode is >= 200 and < 300 && !HasErrors;
}

