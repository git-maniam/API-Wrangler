using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiWrangler.Storage;

public static class CanonicalJson
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, IndentSize = 2, NewLine = "\n" };
    public static byte[] Serialize(JsonNode node) => Encoding.UTF8.GetBytes(Sort(node)!.ToJsonString(Options) + "\n");
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static JsonObject Parse(ReadOnlyMemory<byte> bytes)
    {
        // Accept imported UTF-8 BOMs but always write BOM-free canonical UTF-8.
        if (bytes.Span.StartsWith(Encoding.UTF8.Preamble)) bytes = bytes[Encoding.UTF8.Preamble.Length..];
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        CheckUniqueProperties(document.RootElement);
        return JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject ?? throw new JsonException("Expected a JSON object.");
    }

    private static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject value => new JsonObject(value.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Sort(p.Value)))),
        JsonArray value => new JsonArray(value.Select(Sort).ToArray()),
        _ => node?.DeepClone()
    };

    private static void CheckUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("Duplicate JSON property.");
                CheckUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckUniqueProperties(item);
    }
}
