using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiWrangler.Domain;

namespace ApiWrangler.Storage;

internal sealed record SnapshotFile(string Path, string Hash, long Length);
internal static class ProjectSnapshots
{
    public static async Task<SnapshotInfo> CreateAsync(string root, string state, Func<IEnumerable<string>> enumerate, CancellationToken token)
    {
        var container = Path.Combine(state, "snapshots");
        Directory.CreateDirectory(container);
        var temporary = Path.Combine(container, Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(temporary);
        try
        {
            var beforeGit = await GitInfoAsync(root, token).ConfigureAwait(false);
            var files = enumerate().Order(StringComparer.Ordinal).ToArray();
            var entries = new List<SnapshotFile>();
            foreach (var source in files)
            {
                var relative = Path.GetRelativePath(root, source).Replace('\\', '/');
                var target = ProjectPaths.Resolve(temporary, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                var buffer = new byte[65536];
                long length = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    length += count;
                }
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                entries.Add(new(relative, Convert.ToHexStringLower(hash.GetHashAndReset()), length));
            }
            if (!files.SequenceEqual(enumerate().Order(StringComparer.Ordinal), StringComparer.Ordinal)) throw new ProjectConflictException("project file inventory");
            foreach (var entry in entries)
                if (await FileTransactions.HashFileAsync(ProjectPaths.Resolve(root, entry.Path), token).ConfigureAwait(false) != entry.Hash)
                    throw new ProjectConflictException(entry.Path);
            var afterGit = await GitInfoAsync(root, token).ConfigureAwait(false);
            if (beforeGit != afterGit) throw new ProjectConflictException("Git project state");
            var assets = new List<ProjectAsset>();
            foreach (var entry in entries)
                if (AssetValidator.KindFor(entry.Path) is { } kind)
                    assets.Add(new(entry.Path, kind, CanonicalJson.Parse(await File.ReadAllBytesAsync(ProjectPaths.Resolve(temporary, entry.Path), token).ConfigureAwait(false)), entry.Hash));
            var diagnostics = new AssetValidator().Validate(temporary, assets);
            if (diagnostics.Count > 0) throw new ProjectValidationException(diagnostics);
            var id = CanonicalJson.Hash(JsonSerializer.SerializeToUtf8Bytes(entries));
            var final = Path.Combine(container, id);
            if (Directory.Exists(final))
            {
                // A previously captured snapshot is reusable only while every retained file still matches.
                foreach (var entry in entries)
                    if (await FileTransactions.HashFileAsync(ProjectPaths.Resolve(final, entry.Path), token).ConfigureAwait(false) != entry.Hash)
                        throw new IOException("An existing snapshot was modified. Preserve it for diagnostics before creating another.");
            }
            else
            {
                var metadata = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, id, files = entries });
                await FileTransactions.WriteDurableAsync(Path.Combine(temporary, "snapshot.json"), metadata, token).ConfigureAwait(false);
                Directory.Move(temporary, final);
            }
            return new(id, final, entries.Count, afterGit.Commit, afterGit.Dirty);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
    }

    private static async Task<(string? Commit, bool? Dirty)> GitInfoAsync(string root, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var commit = await GitAsync(root, ["rev-parse", "--verify", "HEAD"], timeout.Token).ConfigureAwait(false);
            if (commit.Exit != 0) return (null, null);
            var status = await GitAsync(root, ["status", "--porcelain=v1", "--untracked-files=normal", "--", "."], timeout.Token).ConfigureAwait(false);
            return (commit.Text.Trim(), status.Exit == 0 ? status.Text.Length != 0 : null);
        }
        catch (System.ComponentModel.Win32Exception) { return (null, null); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return (null, null); }
    }

    private static async Task<(int Exit, string Text)> GitAsync(string root, string[] arguments, CancellationToken token)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.fsmonitor=false");
        start.ArgumentList.Add("--no-optional-locks");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Git metadata process did not start.");
        var output = ReadPrefixAsync(process.StandardOutput, token);
        var errors = ReadPrefixAsync(process.StandardError, token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(output, errors).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private static async Task<string> ReadPrefixAsync(StreamReader input, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            if (result.Length < 4096) result.Append(buffer, 0, Math.Min(count, 4096 - result.Length));
        return result.ToString();
    }
}
