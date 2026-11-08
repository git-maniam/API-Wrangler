using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiWrangler.Execution;

public enum GitConflictChoice { EditedText, Local, Incoming, Delete, SaveBoth }

public sealed class GitConflictSnapshot
{
    internal GitConflictSnapshot(string root, string path, string state, byte[]? baseBytes, byte[]? local,
        byte[]? incoming, string mode, string? proposal, IReadOnlyList<JsonMergeConflict> conflicts)
    {
        RepositoryRoot = root; Path = path; StateToken = state; BaseBytes = baseBytes; LocalBytes = local;
        IncomingBytes = incoming; Mode = mode; ProposalText = proposal; Conflicts = conflicts;
    }
    public string RepositoryRoot { get; }
    public string Path { get; }
    public string StateToken { get; }
    public byte[]? BaseContent => BaseBytes?.ToArray();
    public byte[]? LocalContent => LocalBytes?.ToArray();
    public byte[]? IncomingContent => IncomingBytes?.ToArray();
    public string? BaseText => GitConflictResolutionService.DecodeText(BaseBytes);
    public string? LocalText => GitConflictResolutionService.DecodeText(LocalBytes);
    public string? IncomingText => GitConflictResolutionService.DecodeText(IncomingBytes);
    public bool IsBinary => (BaseBytes is not null && BaseText is null) ||
        (LocalBytes is not null && LocalText is null) || (IncomingBytes is not null && IncomingText is null);
    public string? ProposalText { get; }
    public IReadOnlyList<JsonMergeConflict> Conflicts { get; }
    internal byte[]? BaseBytes { get; }
    internal byte[]? LocalBytes { get; }
    internal byte[]? IncomingBytes { get; }
    internal string Mode { get; }
}

public sealed class GitConflictPreview
{
    internal GitConflictPreview(GitConflictSnapshot snapshot, GitConflictChoice choice, byte[]? content,
        string? additionalPath, byte[]? additionalContent, string diff)
    {
        Snapshot = snapshot; Choice = choice; Bytes = content; AdditionalPath = additionalPath;
        AdditionalBytes = additionalContent; FinalDiff = diff;
        ReviewHash = GitConflictResolutionService.Hash(Encoding.UTF8.GetBytes(snapshot.StateToken + "\n" +
            choice + "\n" + GitConflictResolutionService.Hash(content) + "\n" + additionalPath + "\n" +
            GitConflictResolutionService.Hash(additionalContent) + "\n" + diff));
    }
    public GitConflictSnapshot Snapshot { get; }
    public GitConflictChoice Choice { get; }
    public string FinalDiff { get; }
    public string ReviewHash { get; }
    public byte[]? Content => Bytes?.ToArray();
    public string? AdditionalPath { get; }
    public byte[]? AdditionalContent => AdditionalBytes?.ToArray();
    internal byte[]? Bytes { get; }
    internal byte[]? AdditionalBytes { get; }
}

public sealed record GitConflictValidationContext(string RepositoryRoot, string Path, byte[]? Content,
    string? AdditionalPath, byte[]? AdditionalContent);

/// <summary>Reviews explicit conflict choices, retains recovery data, then stages precisely the reviewed bytes.
/// Git filters, hooks, scripts and merge completion are never invoked by this service.</summary>
public sealed class GitConflictResolutionService(string executable = "git")
{
    private const int MaxBytes = 8 * 1024 * 1024;
    private const int MaxDiffCharacters = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string git = executable;

    public async Task<IReadOnlyList<string>> ListConflictsAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        string root = await RootAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        string output = await RunAsync(root, ["diff", "--name-only", "--diff-filter=U", "-z", "--", "api-wrangler/"], cancellationToken).ConfigureAwait(false);
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal).ToArray();
    }

    public async Task<GitConflictSnapshot> ReadAsync(string repositoryPath, string path, CancellationToken cancellationToken = default)
    {
        string root = await RootAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        path = ValidatePath(root, path);
        string state = await StateAsync(root, path, cancellationToken).ConfigureAwait(false);
        string stages = await StagesAsync(root, path, cancellationToken).ConfigureAwait(false);
        var entries = ParseStages(stages);
        if (entries.Count == 0) throw new InvalidOperationException("Git no longer reports this path as unmerged.");
        if (entries.Values.Any(value => value.Mode is not ("100644" or "100755")))
            throw new InvalidOperationException("Resolve symbolic-link and submodule conflicts with Git outside this editor.");
        byte[]? baseBytes = await ReadStageAsync(1).ConfigureAwait(false);
        byte[]? local = await ReadStageAsync(2).ConfigureAwait(false);
        byte[]? incoming = await ReadStageAsync(3).ConfigureAwait(false);
        string? proposal = null;
        IReadOnlyList<JsonMergeConflict> conflicts = [];
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && baseBytes is not null && local is not null && incoming is not null)
        {
            try
            {
                var merged = JsonThreeWayMerge.CreateProposal(JsonNode.Parse(baseBytes)!, JsonNode.Parse(local)!, JsonNode.Parse(incoming)!);
                proposal = merged.Proposal.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
                conflicts = merged.Conflicts;
            }
            catch (Exception exception) when (exception is JsonException or ArgumentNullException)
            {
                conflicts = [new("$", "A version is not a JSON object/value suitable for a proposal; edit the result explicitly.")];
            }
        }
        if (state != await StateAsync(root, path, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Git or the working file changed while reading this conflict. Reload it.");
        return new(root, path, state, baseBytes, local, incoming,
            entries.GetValueOrDefault(2, entries.GetValueOrDefault(3, entries.Values.First())).Mode, proposal, conflicts);

        async Task<byte[]?> ReadStageAsync(int stage) => entries.TryGetValue(stage, out var entry)
            ? await ReadBlobAsync(root, entry.Hash, cancellationToken).ConfigureAwait(false) : null;
    }

    public async Task<GitConflictPreview> CreatePreviewAsync(GitConflictSnapshot snapshot, GitConflictChoice choice,
        string? editedText = null, string? saveBothPath = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await EnsureCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false);
        byte[]? bytes = choice switch
        {
            GitConflictChoice.EditedText when !snapshot.IsBinary => StrictUtf8.GetBytes(editedText ?? throw new ArgumentException("Enter the resolved text.")),
            GitConflictChoice.Local => snapshot.LocalContent,
            GitConflictChoice.Incoming => snapshot.IncomingContent,
            GitConflictChoice.Delete => null,
            GitConflictChoice.SaveBoth when snapshot.LocalBytes is not null && snapshot.IncomingBytes is not null => snapshot.LocalContent,
            _ => throw new ArgumentException("Choose an existing binary version, save both existing versions, or edit text.")
        };
        if (bytes?.Length > MaxBytes) throw new IOException("Conflict result exceeds the 8 MiB editor limit.");
        string? additionalPath = null;
        byte[]? additional = null;
        if (choice == GitConflictChoice.SaveBoth)
        {
            additionalPath = ValidatePath(snapshot.RepositoryRoot, saveBothPath ?? "");
            await EnsureNewPathAsync(snapshot.RepositoryRoot, additionalPath, cancellationToken).ConfigureAwait(false);
            additional = snapshot.IncomingContent;
        }
        ValidateJson(snapshot.Path, bytes);
        if (additionalPath is not null) ValidateJson(additionalPath, additional);
        string diff = await FinalDiffAsync(snapshot, bytes, cancellationToken).ConfigureAwait(false);
        if (additionalPath is not null) diff += $"\nSave incoming version as {additionalPath}: {Hash(additional)} ({additional!.Length} bytes).\n";
        return new(snapshot, choice, bytes, additionalPath, additional, diff);
    }

    public async Task<string> ResolveAsync(GitConflictPreview preview, string confirmedReviewHash,
        Func<GitConflictValidationContext, CancellationToken, Task> validate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(validate);
        if (confirmedReviewHash != preview.ReviewHash) throw new InvalidOperationException("Confirm the displayed final diff before resolving.");
        GitConflictSnapshot snapshot = preview.Snapshot;
        await EnsureCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false);
        ValidateJson(snapshot.Path, preview.Bytes);
        if (preview.AdditionalPath is not null)
        {
            await EnsureNewPathAsync(snapshot.RepositoryRoot, preview.AdditionalPath, cancellationToken).ConfigureAwait(false);
            ValidateJson(preview.AdditionalPath, preview.AdditionalBytes);
        }
        await validate(new(snapshot.RepositoryRoot, snapshot.Path, preview.Content, preview.AdditionalPath, preview.AdditionalContent), cancellationToken).ConfigureAwait(false);
        await EnsureCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false);
        string index = await GitPathAsync(snapshot.RepositoryRoot, "index", cancellationToken).ConfigureAwait(false);
        RejectReparseAncestors(index);
        string lockPath = index + ".lock";
        string temporaryIndex = index + ".api-wrangler-" + Guid.NewGuid().ToString("N");
        string recovery = Path.Combine(Path.GetDirectoryName(index)!, "api-wrangler-recovery", Guid.NewGuid().ToString("N"));
        bool published = false;
        FileStream? indexLock = null;
        try
        {
            indexLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await EnsureCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false);
            if (preview.AdditionalPath is not null)
                await EnsureNewPathAsync(snapshot.RepositoryRoot, preview.AdditionalPath, cancellationToken).ConfigureAwait(false);
            File.Copy(index, temporaryIndex);
            await StageAsync(snapshot.Path, preview.Bytes, snapshot.Mode).ConfigureAwait(false);
            if (preview.AdditionalPath is not null) await StageAsync(preview.AdditionalPath, preview.AdditionalBytes, "100644").ConfigureAwait(false);
            RejectReparseAncestors(recovery);
            Directory.CreateDirectory(recovery);
            await File.WriteAllTextAsync(Path.Combine(recovery, "manifest.json"), JsonSerializer.Serialize(new
            {
                snapshot.RepositoryRoot,
                snapshot.Path,
                snapshot.StateToken,
                preview.Choice,
                preview.ReviewHash,
                preview.AdditionalPath,
                BaseMissing = snapshot.BaseBytes is null,
                LocalMissing = snapshot.LocalBytes is null,
                IncomingMissing = snapshot.IncomingBytes is null,
                ResultDeleted = preview.Bytes is null
            }), cancellationToken).ConfigureAwait(false);
            await SaveRecoveryAsync("base", snapshot.BaseBytes).ConfigureAwait(false);
            await SaveRecoveryAsync("local", snapshot.LocalBytes).ConfigureAwait(false);
            await SaveRecoveryAsync("incoming", snapshot.IncomingBytes).ConfigureAwait(false);
            await SaveRecoveryAsync("result", preview.Bytes).ConfigureAwait(false);
            byte[]? original = ReadWorkingFile(snapshot.RepositoryRoot, snapshot.Path);
            await SaveRecoveryAsync("working", original).ConfigureAwait(false);
            File.Copy(index, Path.Combine(recovery, "index"));
            await File.WriteAllTextAsync(Path.Combine(recovery, "final.diff"), preview.FinalDiff, cancellationToken).ConfigureAwait(false);
            // Validation and all cancellable preparation finish before changing the working tree.
            cancellationToken.ThrowIfCancellationRequested();
            await EnsureCurrentAsync(snapshot, cancellationToken).ConfigureAwait(false);
            WriteWorkingFile(snapshot.RepositoryRoot, snapshot.Path, preview.Bytes);
            if (preview.AdditionalPath is not null) WriteWorkingFile(snapshot.RepositoryRoot, preview.AdditionalPath, preview.AdditionalBytes, createOnly: true);
            byte[] stagedIndex = await File.ReadAllBytesAsync(temporaryIndex, CancellationToken.None).ConfigureAwait(false);
            await indexLock.WriteAsync(stagedIndex, CancellationToken.None).ConfigureAwait(false);
            indexLock.Flush(flushToDisk: true);
            indexLock.Dispose();
            indexLock = null;
            File.Move(lockPath, index, overwrite: true);
            published = true;
            return recovery;
        }
        finally
        {
            indexLock?.Dispose();
            if (!published && indexLock is not null && File.Exists(lockPath)) File.Delete(lockPath);
            if (File.Exists(temporaryIndex)) File.Delete(temporaryIndex);
        }

        async Task SaveRecoveryAsync(string name, byte[]? bytes)
        {
            if (bytes is not null) await File.WriteAllBytesAsync(Path.Combine(recovery, name), bytes, cancellationToken).ConfigureAwait(false);
        }
        async Task StageAsync(string path, byte[]? bytes, string mode)
        {
            if (bytes is null)
            {
                await RunIndexAsync(snapshot.RepositoryRoot, temporaryIndex, ["update-index", "--force-remove", "--", path], cancellationToken).ConfigureAwait(false);
                return;
            }
            string hash = Encoding.UTF8.GetString(await BinaryGitAsync(snapshot.RepositoryRoot, ["hash-object", "-w", "--stdin"], bytes, cancellationToken).ConfigureAwait(false)).Trim();
            await RunIndexAsync(snapshot.RepositoryRoot, temporaryIndex, ["update-index", "--add", "--cacheinfo", mode, hash, path], cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string Hash(byte[]? bytes) => bytes is null ? "absent" : Convert.ToHexString(SHA256.HashData(bytes));
    internal static string? DecodeText(byte[]? bytes)
    {
        if (bytes is null || bytes.Contains((byte)0)) return null;
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    private async Task EnsureCurrentAsync(GitConflictSnapshot snapshot, CancellationToken token)
    {
        ValidatePath(snapshot.RepositoryRoot, snapshot.Path);
        if (snapshot.StateToken != await StateAsync(snapshot.RepositoryRoot, snapshot.Path, token).ConfigureAwait(false))
            throw new InvalidOperationException("Git state or the working file changed after review. Reload the conflict and review again.");
    }

    private async Task<string> StateAsync(string root, string path, CancellationToken token)
    {
        string index = await GitPathAsync(root, "index", token).ConfigureAwait(false);
        RejectReparseAncestors(index);
        string identity = await RunAsync(root, ["rev-parse", "HEAD"], token).ConfigureAwait(false);
        identity += await RunAsync(root, ["rev-parse", "--abbrev-ref", "HEAD"], token).ConfigureAwait(false);
        identity += await StagesAsync(root, path, token).ConfigureAwait(false);
        identity += Hash(await File.ReadAllBytesAsync(index, token).ConfigureAwait(false));
        identity += Hash(ReadWorkingFile(root, path));
        foreach (string marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge/onto", "rebase-merge/orig-head", "rebase-merge/msgnum", "rebase-apply/onto", "rebase-apply/orig-head", "rebase-apply/next" })
        {
            string location = await GitPathAsync(root, marker, token).ConfigureAwait(false);
            RejectReparseAncestors(location);
            identity += marker + Hash(File.Exists(location) ? await File.ReadAllBytesAsync(location, token).ConfigureAwait(false) : null);
        }
        return Hash(Encoding.UTF8.GetBytes(identity));
    }

    private static Dictionary<int, (string Mode, string Hash)> ParseStages(string output)
    {
        var entries = new Dictionary<int, (string, string)>();
        foreach (string record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = record.IndexOf('\t');
            string[] fields = record[..tab].Split(' ');
            entries.Add(int.Parse(fields[2], System.Globalization.CultureInfo.InvariantCulture), (fields[0], fields[1]));
        }
        return entries;
    }

    private static void ValidateJson(string path, byte[]? bytes)
    {
        if (bytes is null || !path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return;
        JsonNode? value = JsonNode.Parse(bytes);
        if (value is null) throw new InvalidDataException("A project JSON result cannot be null.");
        var check = JsonThreeWayMerge.CreateProposal(value, value, value);
        if (!check.IsConflictFree) throw new InvalidDataException("Duplicate IDs must be resolved before marking this JSON conflict resolved.");
    }

    private async Task<string> FinalDiffAsync(GitConflictSnapshot snapshot, byte[]? bytes, CancellationToken token)
    {
        string folder = Path.Combine(Path.GetTempPath(), "api-wrangler-conflict-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var text = new StringBuilder($"Resolved path: {snapshot.Path}\nResult: {Hash(bytes)} ({bytes?.Length ?? 0} bytes; {(bytes is null ? "deleted" : "present")})\n");
            await File.WriteAllBytesAsync(Path.Combine(folder, "result"), bytes ?? [], token).ConfigureAwait(false);
            foreach (var side in new[] { ("local", snapshot.LocalBytes), ("incoming", snapshot.IncomingBytes) })
            {
                await File.WriteAllBytesAsync(Path.Combine(folder, side.Item1), side.Item2 ?? [], token).ConfigureAwait(false);
                ProcessResult diff = await ProcessRunner.RunAsync(git, ["diff", "--no-index", "--no-ext-diff", "--no-textconv", "--binary", "--", side.Item1, "result"], folder, token, MaxDiffCharacters).ConfigureAwait(false);
                if (diff.ExitCode is not (0 or 1)) throw new IOException("Git could not produce the final conflict diff.");
                text.AppendLine($"{side.Item1}: {Hash(side.Item2)}").Append(diff.StandardOutput);
                if (text.Length > MaxDiffCharacters) throw new IOException("Conflict diff exceeds the 4 Mi-character review limit.");
            }
            return text.ToString();
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private async Task EnsureNewPathAsync(string root, string path, CancellationToken token)
    {
        string normalized = ValidatePath(root, path);
        if (File.Exists(Path.Combine(root, normalized)) || Directory.Exists(Path.Combine(root, normalized)) ||
            (await RunAsync(root, ["ls-files", "-z", "--", normalized], token).ConfigureAwait(false)).Length > 0)
            throw new IOException("Save both requires a new, untracked destination path.");
    }

    private static string ValidatePath(string root, string path)
    {
        string normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith("api-wrangler/", StringComparison.Ordinal) || Path.IsPathRooted(path) ||
            normalized.Split('/').Any(part => part.Length == 0 || part is "." or ".." || part.Contains(':') || part.Any(char.IsControl)))
            throw new ArgumentException("Choose a relative file beneath repository-root api-wrangler/.");
        RejectReparseAncestors(Path.Combine(root, normalized));
        return normalized;
    }

    private static void RejectReparseAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Conflict operations do not follow symbolic links or reparse points.");
        }
    }

    private static byte[]? ReadWorkingFile(string root, string path)
    {
        ValidatePath(root, path);
        string full = Path.Combine(root, path);
        if (Directory.Exists(full)) throw new IOException("The conflict path is a directory.");
        if (!File.Exists(full)) return null;
        if (new FileInfo(full).Length > MaxBytes) throw new IOException("Conflict file exceeds the 8 MiB editor limit.");
        byte[] bytes = File.ReadAllBytes(full);
        if (bytes.Length > MaxBytes) throw new IOException("Conflict file exceeds the 8 MiB editor limit.");
        return bytes;
    }

    private static void WriteWorkingFile(string root, string path, byte[]? bytes, bool createOnly = false)
    {
        ValidatePath(root, path);
        string full = Path.Combine(root, path);
        if (bytes is null) { File.Delete(full); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (createOnly)
        {
            using var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(bytes);
            file.Flush(flushToDisk: true);
            return;
        }
        string temporary = full + ".api-wrangler-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private Task<string> RootAsync(string root, CancellationToken token) => new GitRepositoryService(git).DiscoverRepositoryAsync(root, token);
    private Task<string> StagesAsync(string root, string path, CancellationToken token) => RunAsync(root, ["ls-files", "--unmerged", "-z", "--", path], token);
    private async Task<string> GitPathAsync(string root, string name, CancellationToken token) => Path.GetFullPath((await RunAsync(root, ["rev-parse", "--git-path", name], token).ConfigureAwait(false)).Trim(), root);
    private async Task<string> RunAsync(string root, string[] args, CancellationToken token)
    {
        ProcessResult result = await ProcessRunner.RunAsync(git, ["--literal-pathspecs", .. args], root, token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new IOException($"Git conflict operation failed: {result.StandardError.Trim()}");
        return result.StandardOutput;
    }
    private async Task RunIndexAsync(string root, string index, string[] args, CancellationToken token)
    {
        await BinaryGitAsync(root, args, null, token, index).ConfigureAwait(false);
    }
    private async Task<byte[]> ReadBlobAsync(string root, string hash, CancellationToken token)
    {
        string size = await RunAsync(root, ["cat-file", "-s", hash], token).ConfigureAwait(false);
        if (!long.TryParse(size.Trim(), out long length) || length > MaxBytes) throw new IOException("Conflict version exceeds the 8 MiB editor limit.");
        return await BinaryGitAsync(root, ["cat-file", "blob", hash], null, token).ConfigureAwait(false);
    }

    private async Task<byte[]> BinaryGitAsync(string root, string[] args, byte[]? input, CancellationToken token, string? index = null)
    {
        var start = new ProcessStartInfo(git)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null
        };
        start.ArgumentList.Add("--literal-pathspecs");
        foreach (string argument in args) start.ArgumentList.Add(argument);
        if (index is not null) start.Environment["GIT_INDEX_FILE"] = index;
        using Process process = Process.Start(start) ?? throw new IOException("Git could not be started.");
        using var output = new MemoryStream();
        Task read = ReadOutputAsync();
        Task<string> error = process.StandardError.ReadToEndAsync(token);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.BaseStream.WriteAsync(input, token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await Task.WhenAll(read, process.WaitForExitAsync(token)).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException($"Git conflict operation failed: {(await error.ConfigureAwait(false)).Trim()}");
            await error.ConfigureAwait(false);
            return output.ToArray();
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await read.ConfigureAwait(false); } catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
            try { await error.ConfigureAwait(false); } catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
        }
        async Task ReadOutputAsync()
        {
            var buffer = new byte[8192];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaxBytes)
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    throw new IOException("Git conflict output exceeds the 8 MiB limit.");
                }
                output.Write(buffer, 0, count);
            }
        }
    }
}
