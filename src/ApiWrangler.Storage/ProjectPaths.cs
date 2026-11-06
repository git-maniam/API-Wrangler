namespace ApiWrangler.Storage;

internal static class ProjectPaths
{
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\') || relative.Contains(':'))
            throw new IOException("Project paths must be relative and use forward slashes.");
        var parts = relative.Split('/');
        if (parts.Any(p => p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new IOException("Invalid project path segment.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, comparison))
            throw new IOException("Project path escapes its root.");
        RejectLinks(root);
        var current = Path.GetFullPath(root);
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            RejectLinks(current);
        }
        return full;
    }

    public static void RejectLinks(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Symbolic links and junctions are not allowed inside project storage.");
    }

    public static IEnumerable<string> Enumerate(string root)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (Excluded(name)) continue;
            RejectLinks(path);
            if (Directory.Exists(path))
            {
                foreach (var child in Enumerate(path)) yield return child;
            }
            else yield return path;
        }
    }

    public static bool Excluded(string name) => name is ".git" or ".local" or ".cache" or "runs" or "tmp" or "reports" or "node_modules" or ".env" or ".npmrc" or ".netrc" ||
        name.EndsWith(".local.environment.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".secrets.json", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
}
