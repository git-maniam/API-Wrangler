using System.Globalization;
using System.Text.Json;

namespace ApiWrangler.Application;

public enum DatasetColumnType { Preserve, String, Decimal, Boolean }
public sealed record DatasetColumnMapping(string Source, string Target,
    DatasetColumnType Type = DatasetColumnType.Preserve, bool EmptyStringAsNull = false);
public sealed record DatasetPreviewResult(IReadOnlyList<IReadOnlyDictionary<string, JsonElement>> Rows, bool HasMore);

/// <summary>Produces an in-memory conversion preview; never writes or mutates source assets.</summary>
public static class DatasetPreview
{
    public static async Task<DatasetPreviewResult> CreateAsync(
        IAsyncEnumerable<IReadOnlyDictionary<string, JsonElement>> source,
        IReadOnlyList<DatasetColumnMapping> mappings, CultureInfo culture, int maxRows = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxRows, 1000);
        var columns = mappings.ToArray();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mapping in columns)
        {
            if (mapping is null || string.IsNullOrWhiteSpace(mapping.Source)
                || string.IsNullOrWhiteSpace(mapping.Target) || !targets.Add(mapping.Target)
                || !Enum.IsDefined(mapping.Type))
            {
                throw new ArgumentException("Mappings require valid sources, unique targets, and supported types.", nameof(mappings));
            }
        }
        var rows = new List<IReadOnlyDictionary<string, JsonElement>>();
        await foreach (var row in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count == maxRows) { return new(rows.AsReadOnly(), true); }
            var converted = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (columns.Length == 0)
            {
                foreach (var (key, value) in row) { converted.Add(key, value.Clone()); }
            }
            else
            {
                foreach (var mapping in columns)
                {
                    if (!row.TryGetValue(mapping.Source, out var value)) { continue; }
                    try { converted.Add(mapping.Target, ConvertValue(value, mapping, culture)); }
                    catch (Exception error) when (error is FormatException or InvalidOperationException or OverflowException)
                    {
                        // Do not put potentially sensitive cell values in diagnostics.
                        throw new FormatException($"Preview row {rows.Count + 1}, column '{mapping.Target}' cannot convert to {mapping.Type}.");
                    }
                }
            }
            rows.Add(new System.Collections.ObjectModel.ReadOnlyDictionary<string, JsonElement>(converted));
        }
        return new(rows.AsReadOnly(), false);
    }

    public static async IAsyncEnumerable<IReadOnlyDictionary<string, JsonElement>> FromDelimitedAsync(
        IAsyncEnumerable<IReadOnlyDictionary<string, string>> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return row.ToDictionary(pair => pair.Key,
                pair => JsonSerializer.SerializeToElement(pair.Value), StringComparer.Ordinal);
        }
    }

    private static JsonElement ConvertValue(JsonElement value, DatasetColumnMapping mapping, CultureInfo culture)
    {
        if (value.ValueKind == JsonValueKind.Null) { return value.Clone(); }
        if (mapping.EmptyStringAsNull && value.ValueKind == JsonValueKind.String && value.GetString() == "")
        {
            return JsonSerializer.SerializeToElement<string?>(null);
        }
        return mapping.Type switch
        {
            DatasetColumnType.Preserve => value.Clone(),
            DatasetColumnType.String => JsonSerializer.SerializeToElement(
                value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()),
            DatasetColumnType.Decimal => JsonSerializer.SerializeToElement(value.ValueKind == JsonValueKind.Number
                ? value.GetDecimal() : decimal.Parse(value.GetString()!, NumberStyles.Number, culture)),
            DatasetColumnType.Boolean => JsonSerializer.SerializeToElement(value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean() : bool.Parse(value.GetString()!)),
            _ => throw new ArgumentOutOfRangeException(nameof(mapping))
        };
    }
}
