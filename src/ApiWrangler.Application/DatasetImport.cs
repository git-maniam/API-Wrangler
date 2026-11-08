using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

/// <summary>A fully parsed, immutable inline import awaiting explicit acceptance.</summary>
public sealed class PreparedDatasetImport
{
    private readonly string definition;
    public int RowCount { get; }
    public int AddedCaseIds { get; }
    public string Path { get; }
    public string? ExpectedHash { get; }

    internal PreparedDatasetImport(string path, string? expectedHash, string definition, int rowCount, int addedCaseIds)
    {
        Path = path;
        ExpectedHash = expectedHash;
        this.definition = definition;
        RowCount = rowCount;
        AddedCaseIds = addedCaseIds;
    }

    // Each view is detached: editing a preview cannot change what the user accepted.
    public JsonObject GetDefinition() => JsonNode.Parse(definition)!.AsObject();

    public Task CommitAsync(IProjectSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.SaveAsync([new(Path, GetDefinition(), ExpectedHash)], cancellationToken);
    }
}

public static class DatasetImport
{
    /// <summary>Materializes a bounded inline source before any write. Missing IDs require explicit opt-in.</summary>
    public static async Task<PreparedDatasetImport> PrepareAsync(
        IAsyncEnumerable<IReadOnlyDictionary<string, JsonElement>> source,
        string path, Guid datasetId, string name, string? expectedHash = null,
        bool addMissingCaseIds = false, int maxRows = 100_000, int maxSourceBytes = 8 * 1024 * 1024,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (datasetId == Guid.Empty) { throw new ArgumentException("Dataset ID is required.", nameof(datasetId)); }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSourceBytes);
        var rows = new JsonArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var added = 0;
        long bytes = 2;
        await foreach (var row in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count == maxRows) { throw new FormatException("Inline dataset exceeds the configured row limit."); }
            var content = new JsonObject();
            foreach (var (key, value) in row) { content.Add(key, JsonNode.Parse(value.GetRawText())); }
            string caseId;
            if (row.TryGetValue("_caseId", out var existing))
            {
                if (existing.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(existing.GetString()))
                {
                    throw new FormatException($"Row {rows.Count + 1} requires a nonempty string _caseId.");
                }
                caseId = existing.GetString()!;
            }
            else
            {
                if (!addMissingCaseIds) { throw new FormatException($"Row {rows.Count + 1} is missing _caseId; preview explicit ID addition first."); }
                caseId = Guid.NewGuid().ToString("D");
                content["_caseId"] = caseId;
                added++;
            }
            if (!ids.Add(caseId)) { throw new FormatException($"Duplicate _caseId at row {rows.Count + 1}."); }
            bytes += Encoding.UTF8.GetByteCount(content.ToJsonString()) + (rows.Count == 0 ? 0 : 1);
            if (bytes > maxSourceBytes) { throw new FormatException("Inline dataset exceeds the configured source size limit."); }
            rows.Add(content);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var definition = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = datasetId.ToString("D"),
            ["name"] = name,
            ["rowIdColumn"] = "_caseId",
            ["source"] = new JsonObject { ["type"] = "inline", ["rows"] = rows }
        };
        return new(path, expectedHash, definition.ToJsonString(), rows.Count, added);
    }
}
