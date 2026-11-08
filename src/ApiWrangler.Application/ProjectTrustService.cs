using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiWrangler.Application;

public enum ProjectTrustState
{
    Untrusted = 0,
    Trusted = 1
}

public sealed record ProjectTrustRecord(
    string RepositoryRoot,
    ProjectTrustState State,
    DateTimeOffset DecidedAt,
    string? ScriptHash
);

public sealed record ProjectTrustStatus(
    bool IsTrusted,
    bool HasScriptChanges,
    string CurrentScriptHash,
    string? RecordedScriptHash,
    DateTimeOffset? DecidedAt,
    string Description
);

public sealed class UntrustedScriptExecutionException : InvalidOperationException
{
    public UntrustedScriptExecutionException(string message) : base(message) { }
    public UntrustedScriptExecutionException(string message, Exception? innerException) : base(message, innerException) { }
}

public sealed class ProjectTrustService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string storageFile;
    private readonly SemaphoreSlim fileGate = new(1, 1);

    public ProjectTrustService(string? customStorageDirectory = null)
    {
        string dir = customStorageDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApiWrangler", "trust");
        storageFile = Path.Combine(dir, "trust.json");
    }

    public async Task<ProjectTrustStatus> GetTrustStatusAsync(string projectRoot, CancellationToken token = default)
    {
        string normalizedRoot = NormalizePath(projectRoot);
        string currentHash = ComputeScriptHash(normalizedRoot);
        Dictionary<string, ProjectTrustRecord> store = await ReadStoreAsync(token).ConfigureAwait(false);

        if (!store.TryGetValue(normalizedRoot, out ProjectTrustRecord? record) || record.State != ProjectTrustState.Trusted)
        {
            return new ProjectTrustStatus(
                IsTrusted: false,
                HasScriptChanges: false,
                CurrentScriptHash: currentHash,
                RecordedScriptHash: record?.ScriptHash,
                DecidedAt: record?.DecidedAt,
                Description: "Project is not trusted for script execution. Scripts execute with full local user access without a security sandbox. Explicit trust is required before running scripts or installing packages."
            );
        }

        bool changed = record.ScriptHash is not null && !string.Equals(record.ScriptHash, currentHash, StringComparison.Ordinal);
        if (changed)
        {
            return new ProjectTrustStatus(
                IsTrusted: false,
                HasScriptChanges: true,
                CurrentScriptHash: currentHash,
                RecordedScriptHash: record.ScriptHash,
                DecidedAt: record.DecidedAt,
                Description: "Scripts or package manifests in this project have been modified since trust was granted. Review the changes and re-authorize script execution."
            );
        }

        return new ProjectTrustStatus(
            IsTrusted: true,
            HasScriptChanges: false,
            CurrentScriptHash: currentHash,
            RecordedScriptHash: record.ScriptHash,
            DecidedAt: record.DecidedAt,
            Description: "Project is trusted for script execution."
        );
    }

    public async Task SetTrustStateAsync(string projectRoot, ProjectTrustState state, CancellationToken token = default)
    {
        string normalizedRoot = NormalizePath(projectRoot);
        string? hash = state == ProjectTrustState.Trusted ? ComputeScriptHash(normalizedRoot) : null;
        var record = new ProjectTrustRecord(normalizedRoot, state, DateTimeOffset.UtcNow, hash);

        await fileGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Dictionary<string, ProjectTrustRecord> store = await ReadStoreCoreAsync(token).ConfigureAwait(false);
            store[normalizedRoot] = record;
            await WriteStoreCoreAsync(store, token).ConfigureAwait(false);
        }
        finally
        {
            fileGate.Release();
        }
    }

    public async Task RequireTrustAsync(string projectRoot, CancellationToken token = default)
    {
        ProjectTrustStatus status = await GetTrustStatusAsync(projectRoot, token).ConfigureAwait(false);
        if (!status.IsTrusted)
        {
            throw new UntrustedScriptExecutionException(status.Description);
        }
    }

    public static string ComputeScriptHash(string projectRoot)
    {
        if (!Directory.Exists(projectRoot)) return string.Empty;

        var entries = new List<(string RelativePath, byte[] Content)>();

        // 1. Root package manifests
        string[] manifests = ["package.json", "package-lock.json"];
        foreach (string manifest in manifests)
        {
            string full = Path.Combine(projectRoot, manifest);
            if (File.Exists(full))
            {
                entries.Add((manifest, File.ReadAllBytes(full)));
            }
        }

        // 2. JavaScript / TypeScript files
        string[] scriptExtensions = [".js", ".mjs", ".cjs", ".ts"];
        foreach (string file in Directory.EnumerateFiles(projectRoot, "*.*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(projectRoot, file).Replace('\\', '/');
            if (IsExcluded(relative)) continue;

            string ext = Path.GetExtension(file);
            if (scriptExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add((relative, File.ReadAllBytes(file)));
                continue;
            }

            // 3. Request/collection asset embedded scripts
            if (relative.EndsWith(".request.json", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".collection.json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string text = File.ReadAllText(file);
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("preRequestScript", out var pre) && pre.ValueKind == JsonValueKind.String)
                    {
                        entries.Add(($"{relative}#preRequestScript", Encoding.UTF8.GetBytes(pre.GetString()!)));
                    }
                    if (doc.RootElement.TryGetProperty("testScript", out var test) && test.ValueKind == JsonValueKind.String)
                    {
                        entries.Add(($"{relative}#testScript", Encoding.UTF8.GetBytes(test.GetString()!)));
                    }
                }
                catch (JsonException)
                {
                    // If JSON is malformed, include the raw bytes to ensure changes alter the hash
                    entries.Add((relative, File.ReadAllBytes(file)));
                }
            }
        }

        if (entries.Count == 0) return "none";

        entries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.Ordinal));

        using var sha = SHA256.Create();
        foreach (var (path, content) in entries)
        {
            byte[] pathBytes = Encoding.UTF8.GetBytes(path);
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            sha.TransformBlock(content, 0, content.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    private static bool IsExcluded(string relativePath)
    {
        string[] parts = relativePath.Split('/');
        return parts.Any(p => p is ".git" or ".local" or ".cache" or "runs" or "tmp" or "reports" or "node_modules" or "bin" or "obj");
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private async Task<Dictionary<string, ProjectTrustRecord>> ReadStoreAsync(CancellationToken token)
    {
        await fileGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await ReadStoreCoreAsync(token).ConfigureAwait(false);
        }
        finally
        {
            fileGate.Release();
        }
    }

    private async Task<Dictionary<string, ProjectTrustRecord>> ReadStoreCoreAsync(CancellationToken token)
    {
        if (!File.Exists(storageFile)) return new Dictionary<string, ProjectTrustRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(storageFile, token).ConfigureAwait(false);
            var records = JsonSerializer.Deserialize<List<ProjectTrustRecord>>(bytes, JsonOptions);
            return (records ?? []).ToDictionary(r => r.RepositoryRoot, r => r, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, ProjectTrustRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task WriteStoreCoreAsync(Dictionary<string, ProjectTrustRecord> store, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(storageFile)!);
        var list = store.Values.OrderBy(v => v.RepositoryRoot, StringComparer.OrdinalIgnoreCase).ToList();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(list, JsonOptions);
        string tempFile = storageFile + ".tmp-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(tempFile, bytes, token).ConfigureAwait(false);
        File.Move(tempFile, storageFile, overwrite: true);
    }
}

