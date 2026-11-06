using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Domain;
using Json.Schema;

namespace ApiWrangler.Storage;

internal sealed class AssetValidator
{
    internal static readonly IReadOnlyDictionary<string, byte[]> SchemaFiles = LoadSchemas();
    private readonly Dictionary<AssetKind, JsonSchema> schemas = new();

    public AssetValidator()
    {
        foreach (var kind in Enum.GetValues<AssetKind>())
        {
            var name = kind == AssetKind.Manifest ? "wrangler" : kind.ToString().ToLowerInvariant();
            using var document = JsonDocument.Parse(SchemaFiles[name + ".schema.json"]);
            schemas[kind] = JsonSchema.Build(document.RootElement, new BuildOptions { Dialect = Dialect.Draft202012 });
        }
    }

    public static AssetKind? KindFor(string path) => Path.GetFileName(path) switch
    {
        "wrangler.json" when path == "wrangler.json" => AssetKind.Manifest,
        "collection.json" => AssetKind.Collection,
        "dataset.json" => AssetKind.Dataset,
        var name when name.EndsWith(".request.json", StringComparison.Ordinal) => AssetKind.Request,
        var name when name.EndsWith(".folder.json", StringComparison.Ordinal) => AssetKind.Folder,
        var name when name.EndsWith(".environment.json", StringComparison.Ordinal) && !name.EndsWith(".local.environment.json", StringComparison.Ordinal) => AssetKind.Environment,
        _ => null
    };

    public List<ProjectDiagnostic> Validate(string root, IReadOnlyList<ProjectAsset> assets)
    {
        var problems = new List<ProjectDiagnostic>();
        foreach (var asset in assets)
        {
            var results = schemas[asset.Kind].Evaluate(JsonSerializer.SerializeToElement(asset.Content),
                new EvaluationOptions { OutputFormat = OutputFormat.List, RequireFormatValidation = true });
            AddErrors(results, asset, problems);
        }
        if (problems.Count != 0) return problems;
        var manifest = assets.SingleOrDefault(a => a.Kind == AssetKind.Manifest);
        if (manifest is null) { problems.Add(new("wrangler.json", "", "Project manifest is missing.")); return problems; }
        var ids = new Dictionary<Guid, ProjectAsset>();
        foreach (var asset in assets)
        {
            if (!Guid.TryParse(asset.Id, out var id) || id == Guid.Empty)
            {
                problems.Add(new(asset.Path, asset.Kind == AssetKind.Manifest ? "/projectId" : "/id", "Entity ID must be a nonempty UUID."));
                continue;
            }
            if (!ids.TryAdd(id, asset)) problems.Add(new(asset.Path, "/id", $"Duplicate entity ID also used by {ids[id].Path}.", asset.Id));
        }
        var directories = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var entry in manifest.Content["paths"]!.AsObject())
        {
            try
            {
                var relative = entry.Value!.GetValue<string>();
                var full = ProjectPaths.Resolve(root, relative);
                if (relative.Split('/').Any(p => ProjectPaths.Excluded(p) && !(entry.Key == "reports" && p == "reports")) || relative.StartsWith("schemas", StringComparison.Ordinal) || !directories.Add(full))
                    throw new IOException("Reserved or duplicate project directory.");
            }
            catch (IOException) { problems.Add(new(manifest.Path, "/paths/" + entry.Key, "Invalid, linked, reserved, or duplicate project directory.")); }
        }
        foreach (var asset in assets)
        {
            CheckReference(asset, "defaultEnvironmentId", AssetKind.Environment);
            CheckReference(asset, "collectionId", AssetKind.Collection);
            CheckReference(asset, "folderId", AssetKind.Folder);
            CheckReference(asset, "parentFolderId", AssetKind.Folder);
            CheckReferences(asset, "requestIds", AssetKind.Request);
            CheckReferences(asset, "folderIds", AssetKind.Folder);
            if (asset.Kind == AssetKind.Dataset && asset.Content["source"]?["path"] is JsonValue path)
            {
                var relative = (Path.GetDirectoryName(asset.Path)?.Replace('\\', '/') + "/" + path.GetValue<string>()).TrimStart('/');
                try { if (!File.Exists(ProjectPaths.Resolve(root, relative))) throw new IOException(); }
                catch (IOException) { problems.Add(new(asset.Path, "/source/path", "Dataset source is missing or unsafe.", asset.Id)); }
            }
            if (asset.Kind == AssetKind.Folder)
            {
                var seen = new HashSet<Guid>();
                var cursor = asset;
                while (cursor.Content["parentFolderId"] is JsonValue parent && Guid.TryParse(parent.ToString(), out var id) && ids.TryGetValue(id, out var next))
                {
                    if (!seen.Add(id)) { problems.Add(new(asset.Path, "/parentFolderId", "Folder parent references form a cycle.", asset.Id)); break; }
                    cursor = next;
                }
            }
        }
        return problems;

        void CheckReference(ProjectAsset asset, string field, AssetKind kind)
        {
            if (asset.Content[field] is not JsonValue reference) return;
            if (!Guid.TryParse(reference.ToString(), out var id) || !ids.TryGetValue(id, out var target) || target.Kind != kind)
                problems.Add(new(asset.Path, "/" + field, "Reference does not identify an entity of the expected kind.", asset.Id));
        }
        void CheckReferences(ProjectAsset asset, string field, AssetKind kind)
        {
            if (asset.Content[field] is not JsonArray references) return;
            var seen = new HashSet<Guid>();
            for (var i = 0; i < references.Count; i++)
                if (!Guid.TryParse(references[i]?.ToString(), out var id) || !seen.Add(id) || !ids.TryGetValue(id, out var target) || target.Kind != kind)
                    problems.Add(new(asset.Path, $"/{field}/{i}", "Reference is missing, duplicated, or has the wrong entity kind.", asset.Id));
        }
    }

    private static void AddErrors(EvaluationResults result, ProjectAsset asset, List<ProjectDiagnostic> problems)
    {
        if (result.IsValid) return;
        if (result.Errors is not null)
            foreach (var error in result.Errors)
                problems.Add(new(asset.Path, result.InstanceLocation.ToString(), $"Schema rule '{error.Key}' failed.", asset.Id));
        if (result.Details is not null)
            foreach (var child in result.Details) AddErrors(child, asset, problems);
    }

    private static Dictionary<string, byte[]> LoadSchemas()
    {
        var assembly = typeof(AssetValidator).Assembly;
        return assembly.GetManifestResourceNames().Where(n => n.EndsWith(".schema.json", StringComparison.Ordinal)).ToDictionary(
            n => n[(n.LastIndexOf("Schemas.", StringComparison.Ordinal) + "Schemas.".Length)..],
            n => { using var input = assembly.GetManifestResourceStream(n)!; using var buffer = new MemoryStream(); input.CopyTo(buffer); return buffer.ToArray(); });
    }
}
