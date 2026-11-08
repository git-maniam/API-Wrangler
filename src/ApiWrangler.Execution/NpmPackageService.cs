using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Application;

namespace ApiWrangler.Execution;

public sealed record NpmPackageMetadata(
    string? Name,
    string? Version,
    IReadOnlyDictionary<string, string> Dependencies,
    IReadOnlyDictionary<string, string> DevDependencies,
    bool HasLockfile,
    IReadOnlyList<string> DefinedScripts
);

public sealed record NpmInstallResult(
    bool Success,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool UsedIgnoreScripts
);

public sealed class NpmPackageService
{
    private readonly NodeRuntimeService runtimeService;
    private readonly ProjectTrustService trustService;

    public NpmPackageService(NodeRuntimeService? runtime = null, ProjectTrustService? trust = null)
    {
        runtimeService = runtime ?? new NodeRuntimeService();
        trustService = trust ?? new ProjectTrustService();
    }

    public static NpmPackageMetadata ReadPackageMetadata(string projectRoot)
    {
        string packageJsonPath = Path.Combine(projectRoot, "package.json");
        string packageLockPath = Path.Combine(projectRoot, "package-lock.json");

        if (!File.Exists(packageJsonPath))
        {
            return new NpmPackageMetadata(
                Name: null,
                Version: null,
                Dependencies: new Dictionary<string, string>(),
                DevDependencies: new Dictionary<string, string>(),
                HasLockfile: File.Exists(packageLockPath),
                DefinedScripts: Array.Empty<string>()
            );
        }

        try
        {
            byte[] bytes = File.ReadAllBytes(packageJsonPath);
            using var doc = JsonDocument.Parse(bytes);
            JsonElement root = doc.RootElement;

            string? name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? version = root.TryGetProperty("version", out var v) ? v.GetString() : null;

            var dependencies = ExtractStringMap(root, "dependencies");
            var devDependencies = ExtractStringMap(root, "devDependencies");
            var scripts = ExtractKeys(root, "scripts");

            return new NpmPackageMetadata(
                Name: name,
                Version: version,
                Dependencies: dependencies,
                DevDependencies: devDependencies,
                HasLockfile: File.Exists(packageLockPath),
                DefinedScripts: scripts
            );
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Invalid package.json in '{projectRoot}': {ex.Message}", ex);
        }
    }

    public async Task<NpmInstallResult> InstallPackagesAsync(
        string projectRoot,
        bool allowLifecycleScripts = false,
        CancellationToken token = default)
    {
        await trustService.RequireTrustAsync(projectRoot, token).ConfigureAwait(false);

        string packageJson = Path.Combine(projectRoot, "package.json");
        string packageLock = Path.Combine(projectRoot, "package-lock.json");

        if (!File.Exists(packageJson))
            throw new FileNotFoundException("package.json not found in project root.", packageJson);

        if (!File.Exists(packageLock))
            throw new InvalidOperationException("Deterministic installation requires a committed package-lock.json. Run npm install locally to produce a lockfile before installing in Api Wrangler.");

        NodeRuntimeInfo runtime = await runtimeService.DiscoverAsync(token: token).ConfigureAwait(false);
        if (!runtime.IsAvailable || !runtime.IsNpmAvailable || string.IsNullOrEmpty(runtime.NpmPath))
            throw new InvalidOperationException($"Cannot install packages: {runtime.Diagnostics}");

        var arguments = new List<string> { "ci" };
        if (!allowLifecycleScripts)
        {
            arguments.Add("--ignore-scripts");
        }

        ProcessResult result = await ProcessRunner.RunAsync(
            runtime.NpmPath,
            arguments.ToArray(),
            projectRoot,
            token
        ).ConfigureAwait(false);

        return new NpmInstallResult(
            Success: result.ExitCode == 0,
            ExitCode: result.ExitCode,
            StandardOutput: result.StandardOutput,
            StandardError: result.StandardError,
            UsedIgnoreScripts: !allowLifecycleScripts
        );
    }

    public async Task<ProcessResult> RunScriptAsync(
        string projectRoot,
        string scriptName,
        CancellationToken token = default)
    {
        await trustService.RequireTrustAsync(projectRoot, token).ConfigureAwait(false);

        NpmPackageMetadata metadata = ReadPackageMetadata(projectRoot);
        if (!metadata.DefinedScripts.Contains(scriptName, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Script '{scriptName}' is not defined in package.json scripts.", nameof(scriptName));
        }

        NodeRuntimeInfo runtime = await runtimeService.DiscoverAsync(token: token).ConfigureAwait(false);
        if (!runtime.IsAvailable || !runtime.IsNpmAvailable || string.IsNullOrEmpty(runtime.NpmPath))
            throw new InvalidOperationException($"Cannot run script: {runtime.Diagnostics}");

        return await ProcessRunner.RunAsync(
            runtime.NpmPath,
            ["run", scriptName],
            projectRoot,
            token
        ).ConfigureAwait(false);
    }

    private static Dictionary<string, string> ExtractStringMap(JsonElement parent, string propertyName)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parent.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    result[property.Name] = property.Value.GetString()!;
                }
            }
        }
        return result;
    }

    private static List<string> ExtractKeys(JsonElement parent, string propertyName)
    {
        var result = new List<string>();
        if (parent.TryGetProperty(propertyName, out JsonElement element) && element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                result.Add(property.Name);
            }
        }
        return result;
    }
}

