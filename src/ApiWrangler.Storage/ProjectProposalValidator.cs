using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Domain;

namespace ApiWrangler.Storage;

/// <summary>Validates an in-memory project overlay without writing files or acquiring a writer session.</summary>
public static class ProjectProposalValidator
{
    private const int MaxDefinitionBytes = 16 * 1024 * 1024;

    public static async Task<ProjectView> ValidateAsync(string projectRoot,
        IReadOnlyDictionary<string, byte[]?> proposedFiles, CancellationToken token = default)
    {
        string root = Path.GetFullPath(projectRoot);
        ProjectPaths.RejectLinks(root);
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var overlay = new Dictionary<string, byte[]?>(comparer);
        foreach (var (path, bytes) in proposedFiles)
        {
            ProjectPaths.Resolve(root, path);
            if (path.Split('/').Any(ProjectPaths.Excluded)) throw new IOException("Cannot validate a proposal in a reserved project path.");
            if (!overlay.TryAdd(path, bytes?.ToArray())) throw new IOException("Proposal paths identify the same file.");
        }

        var assets = new List<ProjectAsset>();
        var problems = new List<ProjectDiagnostic>();
        var paths = new HashSet<string>(comparer) { "wrangler.json" };
        try
        {
            byte[]? manifestBytes = await ReadAsync("wrangler.json").ConfigureAwait(false);
            if (manifestBytes is null) throw new IOException("A resolution cannot delete the project manifest.");
            JsonObject manifest = CanonicalJson.Parse(manifestBytes);
            if (manifest["paths"] is not JsonObject configured) throw new IOException("Manifest paths are missing.");
            string[] directories = configured.Where(p => p.Key != "reports").Select(p => p.Value!.GetValue<string>())
                .Concat(["schemas", "scripts", "contracts", "fixtures", "mocks", "webhooks", "monitors", "schedules", "flows", "performance", "applications"])
                .Distinct(comparer).ToArray();
            foreach (string directory in directories)
            {
                if (directory.Split('/').Any(ProjectPaths.Excluded)) throw new IOException("Manifest references a reserved directory.");
                string full = ProjectPaths.Resolve(root, directory);
                if (Directory.Exists(full))
                    foreach (string file in ProjectPaths.Enumerate(full))
                        paths.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                foreach (string path in overlay.Keys)
                    if (path.StartsWith(directory + "/", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) paths.Add(path);
            }
            foreach (string path in overlay.Keys)
                if (AssetValidator.KindFor(path) is not null && !paths.Contains(path))
                    problems.Add(new(path, "", "Proposed asset is outside the configured project directories."));
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                if (AssetValidator.KindFor(path) is not { } kind) continue;
                try
                {
                    byte[]? bytes = await ReadAsync(path).ConfigureAwait(false);
                    if (bytes is not null) assets.Add(new(path, kind, CanonicalJson.Parse(bytes), CanonicalJson.Hash(bytes)));
                }
                catch (JsonException) { problems.Add(new(path, "", "Invalid JSON, duplicate property, or excessive nesting.")); }
                catch (IOException error) { problems.Add(new(path, "", error.Message)); }
            }
            problems.AddRange(new AssetValidator().Validate(root, assets, Exists));
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            problems.Add(new("wrangler.json", "", "Cannot validate project proposal: " + error.Message));
        }
        return new(root, assets, problems.Distinct().ToArray());

        bool Exists(string full)
        {
            string path = Path.GetRelativePath(root, full).Replace('\\', '/');
            return overlay.TryGetValue(path, out byte[]? proposed) ? proposed is not null : File.Exists(full);
        }

        async Task<byte[]?> ReadAsync(string path)
        {
            if (overlay.TryGetValue(path, out byte[]? proposed))
            {
                if (proposed?.Length > MaxDefinitionBytes) throw new IOException("Proposed definition exceeds 16 MiB.");
                return proposed;
            }
            string full = ProjectPaths.Resolve(root, path);
            await using var input = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
            if (input.Length > MaxDefinitionBytes) throw new IOException("Definition exceeds 16 MiB.");
            using var output = new MemoryStream();
            byte[] buffer = new byte[65536];
            int count;
            while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                if (output.Length + count > MaxDefinitionBytes) throw new IOException("Definition exceeds 16 MiB.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
    }
}
