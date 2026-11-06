using System.Text.Json;
using ApiWrangler.Domain;

namespace ApiWrangler.Storage;

internal sealed record FileChange(string Path, byte[] Bytes, string? ExpectedHash);
internal sealed record JournalEntry(string Path, string? OldHash, string NewHash, string Stage, string Backup);
internal sealed record WriteJournal(string State, List<JournalEntry> Entries);

// Journals/backups stay in host-local state. Staged replacement files stay on the target volume.
internal sealed class FileTransactions(string root, string stateDirectory)
{
    private readonly string transactions = Path.Combine(stateDirectory, "transactions");
    internal Action<int>? AfterReplacement { get; set; }

    public async Task ApplyAsync(IReadOnlyList<FileChange> changes, CancellationToken token)
    {
        if (changes.Count == 0) return;
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(transactions, id);
        Directory.CreateDirectory(directory);
        var entries = new List<JournalEntry>();
        var prepared = false;
        try
        {
            for (var index = 0; index < changes.Count; index++)
            {
                var change = changes[index];
                var target = ProjectPaths.Resolve(root, change.Path);
                var old = File.Exists(target) ? await File.ReadAllBytesAsync(target, token).ConfigureAwait(false) : null;
                if ((old is null ? null : CanonicalJson.Hash(old)) != change.ExpectedHash) throw new ProjectConflictException(change.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var stageRelative = (Path.GetDirectoryName(change.Path)?.Replace('\\', '/') + $"/.awr-{id}-{index}.tmp").TrimStart('/');
                var entry = new JournalEntry(change.Path, change.ExpectedHash, CanonicalJson.Hash(change.Bytes), stageRelative, $"{index}.before");
                entries.Add(entry);
                if (old is not null) await WriteDurableAsync(Path.Combine(directory, entry.Backup), old, token).ConfigureAwait(false);
                await WriteDurableAsync(ProjectPaths.Resolve(root, stageRelative), change.Bytes, token).ConfigureAwait(false);
            }
            await WriteJournalAsync(directory, new("prepared", entries), token).ConfigureAwait(false);
            prepared = true;
            for (var index = 0; index < entries.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                var entry = entries[index];
                var target = ProjectPaths.Resolve(root, entry.Path);
                if (await HashFileAsync(target, token).ConfigureAwait(false) != entry.OldHash) throw new ProjectConflictException(entry.Path);
                File.Move(ProjectPaths.Resolve(root, entry.Stage), target, overwrite: true);
                AfterReplacement?.Invoke(index);
            }
            // Cancellation after the final replacement cannot turn a committed transaction into an ambiguous result.
            await WriteJournalAsync(directory, new("committed", entries), CancellationToken.None).ConfigureAwait(false);
            TryArchive(directory);
        }
        catch
        {
            if (prepared) await RollbackAsync(directory, new("prepared", entries)).ConfigureAwait(false);
            else CleanupStages(entries);
            throw;
        }
    }

    public async Task RecoverAsync(CancellationToken token)
    {
        if (!Directory.Exists(transactions)) return;
        foreach (var directory in Directory.EnumerateDirectories(transactions).Order(StringComparer.Ordinal))
        {
            ProjectPaths.RejectLinks(directory);
            var file = Path.Combine(directory, "journal.json");
            if (!File.Exists(file)) continue; // Preparation never published a durable journal; no assets were replaced.
            var journal = JsonSerializer.Deserialize<WriteJournal>(await File.ReadAllBytesAsync(file, token).ConfigureAwait(false))
                ?? throw new IOException("Recovery journal is invalid.");
            ValidateJournal(journal);
            if (journal.State == "committed") { CleanupStages(journal.Entries); TryArchive(directory); }
            else if (journal.State == "prepared") await RollbackAsync(directory, journal).ConfigureAwait(false);
            else throw new IOException("Recovery journal has an unknown state.");
        }
    }

    private async Task RollbackAsync(string directory, WriteJournal journal)
    {
        ValidateJournal(journal);
        // Validate every affected file before restoring any, so recovery never overwrites a third-party edit.
        foreach (var entry in journal.Entries)
        {
            var current = await HashFileAsync(ProjectPaths.Resolve(root, entry.Path), CancellationToken.None).ConfigureAwait(false);
            if (current != entry.OldHash && current != entry.NewHash) throw new ProjectConflictException(entry.Path);
            if (entry.OldHash is not null && await HashFileAsync(Path.Combine(directory, entry.Backup), CancellationToken.None).ConfigureAwait(false) != entry.OldHash)
                throw new IOException("Recovery backup is missing or its hash does not match. Original files were preserved.");
        }
        foreach (var entry in journal.Entries.AsEnumerable().Reverse())
        {
            var target = ProjectPaths.Resolve(root, entry.Path);
            if (await HashFileAsync(target, CancellationToken.None).ConfigureAwait(false) == entry.OldHash) continue;
            if (entry.OldHash is null) File.Delete(target);
            else
            {
                var recovery = target + $".{Guid.NewGuid():N}.tmp";
                await WriteDurableAsync(recovery, await File.ReadAllBytesAsync(Path.Combine(directory, entry.Backup)).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
                File.Move(recovery, target, overwrite: true);
            }
        }
        CleanupStages(journal.Entries);
        await WriteJournalAsync(directory, journal with { State = "committed" }, CancellationToken.None).ConfigureAwait(false);
        TryArchive(directory);
    }

    private void ValidateJournal(WriteJournal journal)
    {
        for (var i = 0; i < journal.Entries.Count; i++)
        {
            var entry = journal.Entries[i];
            ProjectPaths.Resolve(root, entry.Path);
            ProjectPaths.Resolve(root, entry.Stage);
            if (entry.Backup != $"{i}.before" || !Path.GetFileName(entry.Stage).StartsWith(".awr-", StringComparison.Ordinal) || !entry.Stage.EndsWith(".tmp", StringComparison.Ordinal))
                throw new IOException("Recovery journal contains an invalid path.");
        }
    }

    private void CleanupStages(IEnumerable<JournalEntry> entries)
    {
        foreach (var entry in entries) File.Delete(ProjectPaths.Resolve(root, entry.Stage));
    }

    private void TryArchive(string directory)
    {
        // The commit marker is durable. Archival/retention failure must never roll back a committed save.
        try { Archive(directory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void Archive(string directory)
    {
        var recovery = Path.Combine(stateDirectory, "recovery");
        Directory.CreateDirectory(recovery);
        Directory.Move(directory, Path.Combine(recovery, Path.GetFileName(directory)));
        // Keep the ten newest recovery sets; all targets are derived children of this host-local store.
        foreach (var old in Directory.EnumerateDirectories(recovery).OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(10))
        {
            ProjectPaths.RejectLinks(old);
            Directory.Delete(old, recursive: true);
        }
    }

    private static async Task WriteJournalAsync(string directory, WriteJournal journal, CancellationToken token)
    {
        var temporary = Path.Combine(directory, "journal-" + Guid.NewGuid().ToString("N") + ".tmp");
        await WriteDurableAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(journal), token).ConfigureAwait(false);
        File.Move(temporary, Path.Combine(directory, "journal.json"), overwrite: true);
    }

    internal static async Task WriteDurableAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    internal static async Task<string?> HashFileAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
        return Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
}
