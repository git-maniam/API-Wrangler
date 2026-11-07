using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Storage;

namespace ApiWrangler.Desktop;

internal static class ProjectEditorRegressions
{
    public static async Task RunAsync(string directory)
    {
        var root = Path.Combine(directory, "editor-regression-" + Guid.NewGuid().ToString("N"));
        var repository = new ProjectRepository(Path.Combine(directory, "regression-state"));
        var session = new PausedSession(await repository.InitializeAsync(root, "Editor regression"));
        await using var editor = new ProjectEditorViewModel(new ExistingSessionRepository(session));
        editor.ProjectPath = root;
        await editor.OpenProjectCommand.ExecuteAsync(null);
        var asset = editor.SelectedAsset ?? throw new InvalidOperationException("Regression asset was not selected.");
        var original = JsonNode.Parse(editor.EditorText)!.AsObject();
        var saved = (JsonObject)original.DeepClone();
        saved["name"] = "Saved revision";
        editor.EditorText = saved.ToJsonString();
        session.PauseNextSave = true;
        var saving = editor.SaveAssetCommand.ExecuteAsync(null);
        await session.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            if (!editor.IsEditorReadOnly || editor.CanUseDisk || editor.CanUseDraft)
                throw new InvalidOperationException("Editor actions remain available during save.");
            var newer = (JsonObject)saved.DeepClone();
            newer["name"] = "Newer unsaved revision";
            editor.EditorText = newer.ToJsonString();
            await editor.UseDiskCommand.ExecuteAsync(null);
            await editor.UseDraftCommand.ExecuteAsync(null);
            await editor.OpenProjectCommand.ExecuteAsync(null);
            editor.SelectedAsset = editor.Assets.First(a => a.Path != asset.Path);
            if (editor.SelectedAsset?.Path != asset.Path || !editor.IsDirty)
                throw new InvalidOperationException("A competing action changed the in-flight save target.");
        }
        finally { session.ReleaseSave.TrySetResult(); }
        await saving;
        var disk = (await session.ReadAsync()).Assets.Single(a => a.Path == asset.Path);
        var recovered = await session.ReadDraftAsync(asset.Path);
        if (disk.Content["name"]!.GetValue<string>() != "Saved revision" || !editor.IsDirty || editor.HasConflict ||
            !editor.EditorText.Contains("Newer unsaved revision", StringComparison.Ordinal) ||
            recovered?.Text != editor.EditorText || recovered.BaseHash != disk.Hash)
            throw new InvalidOperationException("Save lost newer typing or failed to rebase its recovery draft.");
        await editor.SaveAssetCommand.ExecuteAsync(null);
        if (editor.IsDirty || await session.ReadDraftAsync(asset.Path) is not null)
            throw new InvalidOperationException("The preserved newer revision could not be saved cleanly.");

        var file = Path.Combine(root, asset.Path.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(file, JsonNode.Parse(editor.EditorText)!.ToJsonString());
        editor.EditorText += " ";
        await editor.UseDiskCommand.ExecuteAsync(null);
        var unchangedHash = (await session.ReadAsync()).Assets.Single(a => a.Path == asset.Path).Hash;
        session.PauseNextClear = true;
        saving = editor.SaveAssetCommand.ExecuteAsync(null);
        await session.ClearEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var duringClear = JsonNode.Parse(editor.EditorText)!.AsObject();
            duringClear["name"] = "Typed during recovery clear";
            editor.EditorText = duringClear.ToJsonString();
        }
        finally { session.ReleaseClear.TrySetResult(); }
        await saving;
        recovered = await session.ReadDraftAsync(asset.Path);
        if (!editor.IsDirty || editor.HasConflict || recovered?.Text != editor.EditorText || recovered.BaseHash != unchangedHash)
            throw new InvalidOperationException("A no-op save lost edits during recovery cleanup or changed the noncanonical base hash.");
        await editor.SaveAssetCommand.ExecuteAsync(null);

        var valid = editor.EditorText;
        var invalid = JsonNode.Parse(valid)!.AsObject();
        invalid[asset.Kind == AssetKind.Manifest ? "projectId" : "id"] = Guid.NewGuid().ToString();
        editor.EditorText = invalid.ToJsonString();
        await editor.SaveAssetCommand.ExecuteAsync(null);
        if (!editor.IsDirty || !editor.ProjectStatus.Contains("immutable ID", StringComparison.Ordinal))
            throw new InvalidOperationException("An immutable-ID edit was not reported while preserving the draft.");
        editor.EditorText = valid;

        var external = JsonNode.Parse(valid)!.AsObject();
        external["name"] = "First external revision";
        await File.WriteAllBytesAsync(file, CanonicalJson.Serialize(external));
        await editor.SaveAssetCommand.ExecuteAsync(null);
        if (!editor.HasConflict || !editor.DiskText.Contains("First external revision", StringComparison.Ordinal))
            throw new InvalidOperationException("First external revision was not presented for comparison.");
        external["name"] = "Unseen external revision";
        await File.WriteAllBytesAsync(file, CanonicalJson.Serialize(external));
        await editor.UseDraftCommand.ExecuteAsync(null);
        if (!editor.HasConflict || !editor.DiskText.Contains("Unseen external revision", StringComparison.Ordinal))
            throw new InvalidOperationException("Use draft accepted an unseen disk revision.");
        await editor.SaveAssetCommand.ExecuteAsync(null);
        if (JsonNode.Parse(await File.ReadAllBytesAsync(file))!["name"]!.GetValue<string>() != "Unseen external revision")
            throw new InvalidOperationException("Unconfirmed disk revision was overwritten.");
        await editor.UseDraftCommand.ExecuteAsync(null);
        recovered = await session.ReadDraftAsync(asset.Path);
        disk = (await session.ReadAsync()).Assets.Single(a => a.Path == asset.Path);
        if (editor.HasConflict || recovered?.BaseHash != disk.Hash)
            throw new InvalidOperationException("Confirmed conflict decision was not persisted in recovery.");
        await editor.SaveAssetCommand.ExecuteAsync(null);
        if (editor.IsDirty || editor.HasConflict)
            throw new InvalidOperationException("Explicitly confirmed draft could not be saved.");
    }

    private sealed class ExistingSessionRepository(IProjectSession session) : IProjectRepository
    {
        public Task<IProjectSession> InitializeAsync(string root, string name, CancellationToken token = default) => throw new NotSupportedException();
        public Task<IProjectSession> OpenAsync(string path, bool writable = true, CancellationToken token = default) => Task.FromResult(session);
    }

    private sealed class PausedSession(IProjectSession inner) : IProjectSession
    {
        private readonly Channel<bool> changes = Channel.CreateUnbounded<bool>();
        public TaskCompletionSource SaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool PauseNextSave { get; set; }
        public TaskCompletionSource ClearEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseClear { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool PauseNextClear { get; set; }
        public string Root => inner.Root;
        public bool IsReadOnly => inner.IsReadOnly;
        public Task<ProjectView> ReadAsync(CancellationToken token = default) => inner.ReadAsync(token);
        public async Task SaveAsync(IReadOnlyList<AssetEdit> edits, CancellationToken token = default)
        {
            if (PauseNextSave)
            {
                PauseNextSave = false;
                SaveEntered.TrySetResult();
                await ReleaseSave.Task.WaitAsync(token);
            }
            await inner.SaveAsync(edits, token);
        }
        public Task<SnapshotInfo> SnapshotAsync(CancellationToken token = default) => inner.SnapshotAsync(token);
        public Task<SnapshotInfo> SnapshotAsync(IReadOnlyList<EditorDraft>? drafts, CancellationToken token = default) => inner.SnapshotAsync(drafts, token);
        // Real file watcher timing must not determine these interleavings; normal smoke covers notifications.
        public IAsyncEnumerable<bool> WatchAsync(CancellationToken token = default) => changes.Reader.ReadAllAsync(token);
        public Task SaveDraftAsync(EditorDraft draft, CancellationToken token = default) => inner.SaveDraftAsync(draft, token);
        public Task<EditorDraft?> ReadDraftAsync(string path, CancellationToken token = default) => inner.ReadDraftAsync(path, token);
        public async Task ClearDraftAsync(string path, CancellationToken token = default)
        {
            if (PauseNextClear)
            {
                PauseNextClear = false;
                ClearEntered.TrySetResult();
                await ReleaseClear.Task.WaitAsync(token);
            }
            await inner.ClearDraftAsync(path, token);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
