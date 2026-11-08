using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace ApiWrangler.Application;

/// <summary>Streams typed records. Absent properties remain absent; JSON null remains explicit null.</summary>
public static class JsonDatasetReader
{
    public static async IAsyncEnumerable<IReadOnlyDictionary<string, JsonElement>> ReadArrayAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default,
        int maxRecordBytes = 1_048_576)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecordBytes);
        using var bounded = new JsonRecordBoundStream(stream, maxRecordBytes);
        await foreach (var element in JsonSerializer.DeserializeAsyncEnumerable<JsonElement>(
            bounded, cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return ReadRecord(element);
        }
    }

    public static async IAsyncEnumerable<IReadOnlyDictionary<string, JsonElement>> ReadLinesAsync(
        TextReader reader, int maxRecordCharacters = 1_048_576,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecordCharacters);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var first = true;
        var skipLf = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var character = buffer[i];
                if (first)
                {
                    first = false;
                    if (character == '\uFEFF') { continue; }
                }
                if (skipLf)
                {
                    skipLf = false;
                    if (character == '\n') { continue; }
                }
                if (character is '\r' or '\n')
                {
                    yield return ParseLine(line.ToString());
                    line.Clear();
                    skipLf = character == '\r';
                }
                else
                {
                    if (line.Length == maxRecordCharacters)
                    {
                        throw new FormatException("JSONL record exceeds the configured character limit.");
                    }
                    line.Append(character);
                }
            }
        }
        if (line.Length > 0) { yield return ParseLine(line.ToString()); }
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        return ReadRecord(document.RootElement);
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadRecord(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Dataset records must be JSON objects.");
        }
        ValidateProperties(element);
        return element.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.Clone(), StringComparer.Ordinal);
    }

    private static void ValidateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new FormatException("Duplicate JSON property in dataset record.");
                }
                ValidateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) { ValidateProperties(item); }
        }
    }
}
