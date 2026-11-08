using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ApiWrangler.Execution;

public sealed record NodeRuntimeInfo(
    bool IsAvailable,
    string? NodePath,
    string? Version,
    int MajorVersion,
    bool IsSupportedVersion,
    bool IsNpmAvailable,
    string? NpmPath,
    string? NpmVersion,
    string Diagnostics
);

public sealed class NodeRuntimeService
{
    public const int MinimumSupportedMajorVersion = 20;

    public async Task<NodeRuntimeInfo> DiscoverAsync(string? customNodePath = null, CancellationToken token = default)
    {
        string? nodePath = ResolveNodePath(customNodePath);
        if (string.IsNullOrEmpty(nodePath) || !File.Exists(nodePath))
        {
            return new NodeRuntimeInfo(
                IsAvailable: false,
                NodePath: null,
                Version: null,
                MajorVersion: 0,
                IsSupportedVersion: false,
                IsNpmAvailable: false,
                NpmPath: null,
                NpmVersion: null,
                Diagnostics: $"Node.js runtime not found. Node.js LTS ({MinimumSupportedMajorVersion}+) is required to execute JavaScript test/pre-request scripts and manage packages. Set the AWR_NODE environment variable or install Node.js."
            );
        }

        string version = await GetVersionAsync(nodePath, token).ConfigureAwait(false);
        int major = ParseMajorVersion(version);
        bool supported = major >= MinimumSupportedMajorVersion;

        string? npmPath = ResolveNpmPath(nodePath);
        string? npmVersion = null;
        bool npmAvailable = !string.IsNullOrEmpty(npmPath) && File.Exists(npmPath);
        if (npmAvailable)
        {
            npmVersion = await GetVersionAsync(npmPath!, token).ConfigureAwait(false);
        }

        string diagnostics = supported
            ? $"Node.js {version} located at '{nodePath}'. Npm: {(npmAvailable ? $"{npmVersion} at '{npmPath}'" : "Not found")}."
            : $"Node.js version {version} at '{nodePath}' is unsupported. Minimum supported major version is LTS {MinimumSupportedMajorVersion}.";

        return new NodeRuntimeInfo(
            IsAvailable: true,
            NodePath: nodePath,
            Version: version,
            MajorVersion: major,
            IsSupportedVersion: supported,
            IsNpmAvailable: npmAvailable,
            NpmPath: npmPath,
            NpmVersion: npmVersion,
            Diagnostics: diagnostics
        );
    }

    public static string? ResolveNodePath(string? customPath = null)
    {
        if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
            return Path.GetFullPath(customPath);

        string? envPath = Environment.GetEnvironmentVariable("AWR_NODE");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return Path.GetFullPath(envPath);

        string fileName = OperatingSystem.IsWindows() ? "node.exe" : "node";

        string? fromPath = FindInPath(fileName);
        if (fromPath != null) return fromPath;

        if (OperatingSystem.IsWindows())
        {
            string[] standardLocations =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "node", "node.exe"),
                @"C:\Program Files\nodejs\node.exe"
            ];
            foreach (string loc in standardLocations)
            {
                if (File.Exists(loc)) return Path.GetFullPath(loc);
            }
        }
        else
        {
            string[] standardLocations =
            [
                "/usr/local/bin/node",
                "/usr/bin/node",
                "/opt/homebrew/bin/node"
            ];
            foreach (string loc in standardLocations)
            {
                if (File.Exists(loc)) return loc;
            }
        }

        return null;
    }

    public static string? ResolveNpmPath(string? nodePath = null)
    {
        string npmName = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";

        if (!string.IsNullOrEmpty(nodePath) && File.Exists(nodePath))
        {
            string dir = Path.GetDirectoryName(nodePath)!;
            string sibling = Path.Combine(dir, npmName);
            if (File.Exists(sibling)) return sibling;
        }

        string? fromPath = FindInPath(npmName);
        if (fromPath != null) return fromPath;

        if (OperatingSystem.IsWindows())
        {
            string fallback = @"C:\Program Files\nodejs\npm.cmd";
            if (File.Exists(fallback)) return fallback;
        }

        return null;
    }

    private static string? FindInPath(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        char separator = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (string entry in pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(entry.Trim(), fileName);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static async Task<string> GetVersionAsync(string executable, CancellationToken token)
    {
        try
        {
            var psi = new ProcessStartInfo(executable, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process == null) return "unknown";

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            string stdout = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return stdout.Trim();
        }
        catch
        {
            return "unknown";
        }
    }

    public static int ParseMajorVersion(string version)
    {
        if (string.IsNullOrEmpty(version)) return 0;
        var match = Regex.Match(version, @"v?(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out int major) ? major : 0;
    }
}

