using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ApiWrangler.Application;
using ApiWrangler.Domain;

namespace ApiWrangler.Storage;

internal sealed class ProjectSession : IProjectSession
{
    private const int MaxDefinitionBytes = 16 * 1024 * 1024;
    private readonly string stateDirectory;
    private readonly FileStream? lease;
    private readonly FileSystemWatcher watcher;
    private readonly Channel<bool> changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly AssetValidator validator = new();
    private bool disposed;
    internal FileTransactions Transactions { get; }
    public string Root { get; }
    public bool IsReadOnly => lease is null;

    public ProjectSession(string root, string stateDirectory, bool writable)
    {
        Root = Path.GetFullPath(root);
        this.stateDirectory = stateDirectory;
        Directory.CreateDirectory(stateDirectory);
        ProjectPaths.RejectLinks(stateDirectory);
        Transactions = new(Root, stateDirectory);
        if (writable)
        {
            try
            {
                var lockPath = ProjectPaths.Resolve(Root, ".local/writer.lock");
                Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
                lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (IOException) { /* Another writer, unsafe path, or read-only volume: offer read-only access. */ }
            catch (UnauthorizedAccessException) { }
        }
        watcher = new FileSystemWatcher(Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Changed += OnChange;
        watcher.Created += OnChange;
        watcher.Deleted += OnChange;
        watcher.Renamed += OnChange;
        watcher.Error += (_, _) => changes.Writer.TryWrite(true); // Overflow always forces a full rescan.
        watcher.EnableRaisingEvents = true;
    }

    internal Task RecoverAsync(CancellationToken token) => Transactions.RecoverAsync(token);

    public async Task<ProjectView> ReadAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var assets = new List<ProjectAsset>();
        var problems = new List<ProjectDiagnostic>();
        try
        {
            foreach (var file in EnumerateProjectFiles())
            {
                token.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
                if (AssetValidator.KindFor(relative) is not { } kind) continue;
                try
                {
                    var bytes = await ReadDefinitionAsync(file, token).ConfigureAwait(false);
                    assets.Add(new(relative, kind, CanonicalJson.Parse(bytes), CanonicalJson.Hash(bytes)));
                }
                catch (JsonException) { problems.Add(new(relative, "", "Invalid JSON, duplicate property, or excessive nesting.")); }
                catch (IOException) { problems.Add(new(relative, "", "Cannot read asset, or definition exceeds 16 MiB. Use file-backed bodies/datasets.")); }
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        { problems.Add(new("wrangler.json", "/paths", "Cannot enumerate the project. Check the manifest, permissions, and linked paths.")); }
        problems.AddRange(validator.Validate(Root, assets));
        if (Directory.Exists(Path.Combine(stateDirectory, "transactions")) && Directory.EnumerateFiles(Path.Combine(stateDirectory, "transactions"), "journal.json", SearchOption.AllDirectories).Any())
            problems.Add(new("wrangler.json", "", "An incomplete transaction requires recovery by the project writer."));
        return new(Root, assets, problems.Distinct().ToArray());
    }

    public async Task SaveAsync(IReadOnlyList<AssetEdit> edits, CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureWritable();
            EnsureNoGitIntegration();
            var view = await ReadAsync(token).ConfigureAwait(false);
            if (!view.IsValid) throw new ProjectValidationException(view.Diagnostics);
            var manifest = view.Assets.Single(a => a.Kind == AssetKind.Manifest).Content;
            var updated = view.Assets.ToDictionary(a => a.Path, StringComparer.OrdinalIgnoreCase);
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<FileChange>();
            foreach (var edit in edits)
            {
                if (!unique.Add(edit.Path)) throw new ArgumentException("A transaction cannot edit the same file twice.", nameof(edits));
                var kind = AssetValidator.KindFor(edit.Path) ?? throw new ArgumentException("Only supported project asset definitions can be saved.", nameof(edits));
                ProjectPaths.Resolve(Root, edit.Path);
                var baseDirectory = kind switch
                {
                    AssetKind.Collection or AssetKind.Folder or AssetKind.Request => manifest["paths"]!["collections"]!.GetValue<string>(),
                    AssetKind.Environment => manifest["paths"]!["environments"]!.GetValue<string>(),
                    AssetKind.Dataset => manifest["paths"]!["datasets"]!.GetValue<string>(),
                    _ => null
                };
                if (baseDirectory is not null && !edit.Path.StartsWith(baseDirectory + "/", StringComparison.Ordinal))
                    throw new ArgumentException("Asset location does not match its configured project directory.", nameof(edits));
                updated.TryGetValue(edit.Path, out var old);
                if (old?.Hash != edit.ExpectedHash) throw new ProjectConflictException(edit.Path);
                if (old is not null)
                {
                    var field = kind == AssetKind.Manifest ? "projectId" : "id";
                    if (!JsonNode.DeepEquals(old.Content[field], edit.Content[field])) throw new ArgumentException("An entity's immutable ID cannot change.", nameof(edits));
                    if (JsonNode.DeepEquals(old.Content, edit.Content)) continue;
                }
                var bytes = CanonicalJson.Serialize(edit.Content);
                if (bytes.Length > MaxDefinitionBytes) throw new IOException("Definition exceeds 16 MiB. Use file-backed bodies/datasets.");
                updated[edit.Path] = new(edit.Path, kind, (JsonObject)edit.Content.DeepClone(), CanonicalJson.Hash(bytes));
                files.Add(new(edit.Path, bytes, edit.ExpectedHash));
            }
            var problems = validator.Validate(Root, updated.Values.ToArray());
            if (problems.Count != 0) throw new ProjectValidationException(problems);
            EnsureNoGitIntegration();
            await Transactions.ApplyAsync(files, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<SnapshotInfo> SnapshotAsync(CancellationToken token = default)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            EnsureNoGitIntegration();
            var view = await ReadAsync(token).ConfigureAwait(false);
            if (!view.IsValid) throw new ProjectValidationException(view.Diagnostics);
            return await ProjectSnapshots.CreateAsync(Root, stateDirectory, EnumerateProjectFiles, token).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    internal IEnumerable<string> EnumerateProjectFiles()
    {
        var manifestPath = ProjectPaths.Resolve(Root, "wrangler.json");
        yield return manifestPath;
        var bytes = File.ReadAllBytes(manifestPath);
        if (bytes.Length > MaxDefinitionBytes) throw new IOException("Manifest exceeds the definition limit.");
        var manifest = CanonicalJson.Parse(bytes);
        if (manifest["paths"] is not JsonObject configured) throw new IOException("Manifest paths are missing.");
        var directories = configured.Where(p => p.Key != "reports").Select(p => p.Value!.GetValue<string>())
            .Concat(["schemas", "scripts", "contracts", "fixtures", "mocks", "webhooks", "monitors", "schedules", "flows", "performance", "applications"])
            .Distinct(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { manifestPath };
        foreach (var directory in directories)
        {
            if (directory.Split('/').Any(ProjectPaths.Excluded)) throw new IOException("Manifest references a reserved directory.");
            var path = ProjectPaths.Resolve(Root, directory);
            if (!Directory.Exists(path)) continue;
            foreach (var file in ProjectPaths.Enumerate(path))
                if (seen.Add(file)) yield return file;
        }
    }

    public async IAsyncEnumerable<bool> WatchAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        while (await changes.Reader.WaitToReadAsync(token).ConfigureAwait(false))
        {
            await Task.Delay(150, token).ConfigureAwait(false);
            while (changes.Reader.TryRead(out _)) { }
            yield return true;
        }
    }

    private void OnChange(object sender, FileSystemEventArgs args)
    {
        var relative = Path.GetRelativePath(Root, args.FullPath).Replace('\\', '/');
        if (!relative.Split('/').Any(ProjectPaths.Excluded)) changes.Writer.TryWrite(true);
    }

    public async Task SaveDraftAsync(EditorDraft draft, CancellationToken token = default)
    {
        EnsureWritable();
        ProjectPaths.Resolve(Root, draft.Path);
        var data = JsonSerializer.SerializeToUtf8Bytes(draft);
        if (data.Length > MaxDefinitionBytes) throw new IOException("Recovery draft exceeds the definition limit.");
        await WriteStateAsync(DraftPath(draft.Path), data, token).ConfigureAwait(false);
    }

    public async Task<EditorDraft?> ReadDraftAsync(string path, CancellationToken token = default)
    {
        var file = DraftPath(path);
        if (!File.Exists(file)) return null;
        var draft = JsonSerializer.Deserialize<EditorDraft>(await ReadDefinitionAsync(file, token).ConfigureAwait(false));
        if (draft is null || draft.Path != path) throw new IOException("Recovery draft is invalid.");
        return draft;
    }

    public Task ClearDraftAsync(string path, CancellationToken token = default)
    {
        EnsureWritable();
        token.ThrowIfCancellationRequested();
        File.Delete(DraftPath(path));
        return Task.CompletedTask;
    }

    private string DraftPath(string path) => Path.Combine(stateDirectory, "drafts", CanonicalJson.Hash(Encoding.UTF8.GetBytes(path)) + ".json");

    internal static async Task WriteStateAsync(string file, byte[] data, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await FileTransactions.WriteDurableAsync(temporary, data, token).ConfigureAwait(false);
            File.Move(temporary, file, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static async Task<byte[]> ReadDefinitionAsync(string file, CancellationToken token)
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
        if (input.Length > MaxDefinitionBytes) throw new IOException("Asset definition exceeds the size limit.");
        using var buffer = new MemoryStream();
        var bytes = new byte[65536];
        int count;
        while ((count = await input.ReadAsync(bytes, token).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > MaxDefinitionBytes) throw new IOException("Asset definition exceeds the size limit.");
            buffer.Write(bytes, 0, count);
        }
        return buffer.ToArray();
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsReadOnly) throw new ProjectReadOnlyException();
    }

    private void EnsureNoGitIntegration()
    {
        for (var directory = new DirectoryInfo(Root); directory is not null; directory = directory.Parent)
        {
            var git = Path.Combine(directory.FullName, ".git");
            if (File.Exists(git))
            {
                var reference = File.ReadAllText(git).Trim();
                if (!reference.StartsWith("gitdir: ", StringComparison.Ordinal)) throw new IOException("Git worktree metadata is invalid.");
                git = Path.GetFullPath(Path.Combine(directory.FullName, reference[8..]));
            }
            if (!Directory.Exists(git)) continue;
            if (new[] { "index.lock", "MERGE_HEAD", "rebase-merge", "rebase-apply", "CHERRY_PICK_HEAD", "REVERT_HEAD" }.Any(name => File.Exists(Path.Combine(git, name)) || Directory.Exists(Path.Combine(git, name))))
                throw new IOException("Project writes/execution are suspended while Git integration or an index mutation is in progress.");
            break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            watcher.Dispose();
            changes.Writer.TryComplete();
            if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
}
