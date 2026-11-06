using System.Text;
using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;

namespace ApiWrangler.Storage;

public sealed class ProjectRepository : IProjectRepository
{
    private readonly string stateRoot;
    public ProjectRepository(string? stateRoot = null) => this.stateRoot = Path.GetFullPath(stateRoot ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApiWrangler", "projects"));

    public async Task<IProjectSession> InitializeAsync(string root, string name, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Project name is required.", nameof(name));
        root = Path.GetFullPath(root);
        ProjectPaths.RejectLinks(root);
        Directory.CreateDirectory(root);
        var session = new ProjectSession(root, StateDirectory(root), writable: true);
        try
        {
            if (session.IsReadOnly) throw new ProjectReadOnlyException();
            await session.RecoverAsync(token).ConfigureAwait(false);
            if (File.Exists(Path.Combine(root, "wrangler.json"))) throw new IOException("This directory already contains a project.");
            var projectId = Guid.NewGuid();
            var collectionId = Guid.NewGuid();
            var requestId = Guid.NewGuid();
            var environmentId = Guid.NewGuid();
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["wrangler.json"] = CanonicalJson.Serialize(new JsonObject
                {
                    ["$schema"] = "./schemas/wrangler.schema.json", ["schemaVersion"] = 1,
                    ["projectId"] = projectId.ToString(), ["name"] = name, ["defaultEnvironmentId"] = environmentId.ToString(),
                    ["paths"] = new JsonObject { ["collections"] = "collections", ["environments"] = "environments", ["datasets"] = "datasets", ["reports"] = "reports" },
                    ["execution"] = new JsonObject { ["requestTimeoutMs"] = 30_000, ["maxConcurrentRequests"] = 4, ["maxRedirects"] = 10, ["tlsVerification"] = true }
                }),
                ["collections/default/collection.json"] = CanonicalJson.Serialize(new JsonObject
                {
                    ["schemaVersion"] = 1, ["id"] = collectionId.ToString(), ["name"] = "Default collection", ["requestIds"] = new JsonArray(requestId.ToString())
                }),
                ["collections/default/requests/hello.request.json"] = CanonicalJson.Serialize(new JsonObject
                {
                    ["schemaVersion"] = 1, ["id"] = requestId.ToString(), ["collectionId"] = collectionId.ToString(), ["name"] = "Hello API",
                    ["protocol"] = "http", ["method"] = "GET", ["url"] = "http://127.0.0.1:5057/echo", ["headers"] = new JsonArray(), ["query"] = new JsonArray(),
                    ["auth"] = new JsonObject { ["type"] = "none" }, ["body"] = new JsonObject { ["mode"] = "none" }
                }),
                ["environments/local.environment.json"] = CanonicalJson.Serialize(new JsonObject
                {
                    ["schemaVersion"] = 1, ["id"] = environmentId.ToString(), ["name"] = "Local", ["variables"] = new JsonObject(), ["secrets"] = new JsonObject()
                })
            };
            foreach (var schema in AssetValidator.SchemaFiles) files["schemas/" + schema.Key] = schema.Value;
            foreach (var file in files.Keys)
                if (File.Exists(ProjectPaths.Resolve(root, file))) throw new IOException($"Initialization would replace an existing file: {file}");
            var assets = files.Where(p => AssetValidator.KindFor(p.Key) is not null).Select(p => new ProjectAsset(p.Key,
                AssetValidator.KindFor(p.Key)!.Value, CanonicalJson.Parse(p.Value), CanonicalJson.Hash(p.Value))).ToArray();
            var problems = new AssetValidator().Validate(root, assets);
            if (problems.Count != 0) throw new ProjectValidationException(problems);
            var changes = files.Select(p => new FileChange(p.Key, p.Value, null)).ToList();
            var ignore = ProjectPaths.Resolve(root, ".gitignore");
            var old = File.Exists(ignore) ? await File.ReadAllBytesAsync(ignore, token).ConfigureAwait(false) : null;
            var existing = old is null ? "" : Encoding.UTF8.GetString(old);
            var required = new[] { ".local/", ".cache/", "runs/", "tmp/", "*.local.environment.json", "*.secrets.json" };
            var missing = required.Except(existing.Split('\n').Select(l => l.TrimEnd('\r')), StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
            {
                var append = (existing.Length != 0 && !existing.EndsWith('\n') ? "\n" : "") + string.Join('\n', missing) + "\n";
                changes.Add(new(".gitignore", Encoding.UTF8.GetBytes(existing + append), old is null ? null : CanonicalJson.Hash(old)));
            }
            await session.Transactions.ApplyAsync(changes, token).ConfigureAwait(false);
            return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public async Task<IProjectSession> OpenAsync(string path, bool writable = true, CancellationToken token = default)
    {
        var root = Discover(path);
        var session = new ProjectSession(root, StateDirectory(root), writable);
        try
        {
            if (!session.IsReadOnly) await session.RecoverAsync(token).ConfigureAwait(false);
            return session;
        }
        catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
    }

    public static string Discover(string path)
    {
        var selected = Path.GetFullPath(path);
        if (File.Exists(selected)) selected = Path.GetDirectoryName(selected)!;
        for (var directory = new DirectoryInfo(selected); directory is not null; directory = directory.Parent)
        {
            ProjectPaths.RejectLinks(directory.FullName);
            if (File.Exists(Path.Combine(directory.FullName, "wrangler.json"))) return directory.FullName;
            var child = Path.Combine(directory.FullName, "api-wrangler");
            if (File.Exists(Path.Combine(child, "wrangler.json"))) { ProjectPaths.RejectLinks(child); return child; }
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || File.Exists(Path.Combine(directory.FullName, ".git"))) break;
        }
        throw new IOException("No wrangler.json was found in the selected project or repository.");
    }

    internal string StateDirectory(string root)
    {
        var identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
        return Path.Combine(stateRoot, CanonicalJson.Hash(Encoding.UTF8.GetBytes(identity)));
    }
}
