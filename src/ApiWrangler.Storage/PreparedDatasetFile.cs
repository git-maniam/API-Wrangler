using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;

namespace ApiWrangler.Storage;

public sealed class PreparedDatasetFile : IAsyncDisposable
{
    private readonly FileStream spool;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;
    public int RowCount { get; private set; }
    public int AddedCaseIds { get; private set; }
    public string Hash { get; private set; } = "";
    public string Preview { get; private set; } = "";
    private PreparedDatasetFile(FileStream spool) => this.spool = spool;

    public static async Task<PreparedDatasetFile> CreateAsync(
        IAsyncEnumerable<IReadOnlyDictionary<string, JsonElement>> rows,
        bool addMissingIds, CancellationToken token = default)
    {
        var path = Path.Combine(Path.GetTempPath(), $"awr-dataset-{Guid.NewGuid():N}.tmp");
        var result = new PreparedDatasetFile(new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose));
        try
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var preview = new StringBuilder();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await foreach (var row in rows.WithCancellation(token).ConfigureAwait(false))
            {
                token.ThrowIfCancellationRequested();
                if (result.RowCount == 1_000_000) throw new FormatException("Dataset exceeds one million rows.");
                var content = new JsonObject();
                foreach (var (key, value) in row) content[key] = JsonNode.Parse(value.GetRawText());
                string id;
                if (row.TryGetValue("_caseId", out var valueId))
                {
                    if (valueId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(valueId.GetString()))
                        throw new FormatException("Case IDs must be nonempty strings.");
                    id = valueId.GetString()!;
                }
                else
                {
                    if (!addMissingIds) throw new FormatException("Missing _caseId; explicitly enable ID addition before preview.");
                    id = Guid.NewGuid().ToString("D");
                    content["_caseId"] = id;
                    result.AddedCaseIds++;
                }
                if (!ids.Add(id)) throw new FormatException($"Duplicate case ID at row {result.RowCount + 1}.");
                var line = content.ToJsonString() + "\n";
                var bytes = Encoding.UTF8.GetBytes(line);
                if (bytes.Length > 1_048_576 || result.spool.Length + bytes.Length > 1024L * 1024 * 1024)
                    throw new FormatException("Dataset exceeds record (1 MiB) or total (1 GiB) limits.");
                hash.AppendData(bytes);
                await result.spool.WriteAsync(bytes, token).ConfigureAwait(false);
                if (result.RowCount < 100 && preview.Length + line.Length <= 65536) preview.Append(line);
                result.RowCount++;
            }
            await result.spool.FlushAsync(token).ConfigureAwait(false);
            result.Hash = Convert.ToHexStringLower(hash.GetHashAndReset());
            result.Preview = preview.ToString();
            return result;
        }
        catch { await result.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task CommitAsync(IProjectSession session, string path, Guid id, string name,
        string? expectedHash = null, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            spool.Position = 0;
            var definition = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["id"] = id.ToString(),
                ["name"] = name,
                ["rowIdColumn"] = "_caseId"
            };
            await session.ImportDatasetFileAsync(new(path, definition, expectedHash), spool, Hash, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { if (!disposed) { disposed = true; await spool.DisposeAsync().ConfigureAwait(false); } }
        finally { gate.Release(); }
    }
}
