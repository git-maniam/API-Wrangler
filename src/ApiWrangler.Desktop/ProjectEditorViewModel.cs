using System.IO;
using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiWrangler.Desktop;

public partial class ProjectEditorViewModel(IProjectRepository repository) : ObservableObject, IAsyncDisposable
{
    private IProjectSession? session;
    private CancellationTokenSource? watchCancellation;
    private readonly SemaphoreSlim draftGate = new(1, 1);
    private string? baseHash;
    private string? currentPath;
    private bool settingText;
    private int draftRevision;
    private int refreshRevision;
    private bool disposed;
    private bool editingBlocked;

    [ObservableProperty] private string projectPath = "";
    [ObservableProperty] private string projectStatus = "Open a project directory or wrangler.json to edit its assets.";
    [ObservableProperty] private IReadOnlyList<ProjectAsset> assets = [];
    [ObservableProperty] private ProjectAsset? selectedAsset;
    [ObservableProperty] private string editorText = "";
    [ObservableProperty] private string diskText = "";
    [ObservableProperty] private bool hasConflict;
    [ObservableProperty] private bool isDirty;

    public bool CanEdit => session is { IsReadOnly: false } && SelectedAsset is not null && !editingBlocked;
    public bool IsEditorReadOnly => !CanEdit;
    public bool CanUseDisk => IsDirty && session is { IsReadOnly: false };
    public bool CanUseDraft => HasConflict && CanEdit;

    [RelayCommand]
    private async Task OpenProjectAsync()
    {
        if (IsDirty)
        {
            ProjectStatus = "Resolve or save the current draft before opening another project.";
            return;
        }
        try
        {
            watchCancellation?.Cancel();
            if (session is not null) await session.DisposeAsync();
            session = null;
            watchCancellation?.Dispose();
            watchCancellation = null;
            Assets = [];
            SelectedAsset = null;
            currentPath = null;
            baseHash = null;
            editingBlocked = false;
            DiskText = "";
            SetText("");
            session = await repository.OpenAsync(ProjectPath);
            watchCancellation = new CancellationTokenSource();
            await RefreshAsync();
            _ = WatchAsync(session, watchCancellation.Token);
            if (!editingBlocked) ProjectStatus = session.IsReadOnly ? "Project opened read-only: another writer may own it." : "Project opened.";
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(IsEditorReadOnly));
            OnPropertyChanged(nameof(CanUseDisk));
            OnPropertyChanged(nameof(CanUseDraft));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ProjectStatus = error.Message;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(IsEditorReadOnly));
            OnPropertyChanged(nameof(CanUseDisk));
            OnPropertyChanged(nameof(CanUseDraft));
        }
    }

    partial void OnSelectedAssetChanged(ProjectAsset? value)
    {
        if (value is null) return;
        if (IsDirty && currentPath is not null && value.Path != currentPath)
        {
            ProjectStatus = "Save or resolve the current draft before selecting another asset.";
            SelectedAsset = Assets.First(a => a.Path == currentPath);
            return;
        }
        _ = LoadSelectedAsync(value);
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsEditorReadOnly));
        OnPropertyChanged(nameof(CanUseDraft));
    }

    partial void OnHasConflictChanged(bool value) => OnPropertyChanged(nameof(CanUseDraft));
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(CanUseDisk));

    private async Task LoadSelectedAsync(ProjectAsset asset)
    {
        if (session is null) return;
        try
        {
            currentPath = asset.Path;
            var draft = await session.ReadDraftAsync(asset.Path);
            if (SelectedAsset?.Path != asset.Path || IsDirty) return;
            baseHash = draft?.BaseHash ?? asset.Hash;
            SetText(draft?.Text ?? draft?.Content.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ??
                asset.Content.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            DiskText = asset.Content.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            IsDirty = draft is not null;
            HasConflict = draft is not null && draft.BaseHash != asset.Hash;
            if (HasConflict) ProjectStatus = "Recovered draft conflicts with disk. Compare both versions, then choose which to keep.";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ProjectStatus = error.Message;
        }
    }

    partial void OnEditorTextChanged(string value)
    {
        if (settingText || SelectedAsset is null) return;
        IsDirty = true;
        var revision = ++draftRevision;
        _ = PersistDraftAsync(revision, SelectedAsset.Path, value);
    }

    private async Task PersistDraftAsync(int revision, string path, string text)
    {
        try
        {
            await Task.Delay(300);
            await draftGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (revision != draftRevision || session is null || session.IsReadOnly || baseHash is null) return;
                JsonObject content;
                try { content = JsonNode.Parse(text) as JsonObject ?? new JsonObject(); }
                catch (System.Text.Json.JsonException) { content = new JsonObject(); }
                await session.SaveDraftAsync(new(path, baseHash, content, text)).ConfigureAwait(false);
            }
            finally { draftGate.Release(); }
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or ObjectDisposedException)
        {
            if (!disposed) _ = System.Windows.Application.Current.Dispatcher.InvokeAsync(() => ProjectStatus = error.Message);
        }
    }

    [RelayCommand]
    private async Task SaveAssetAsync()
    {
        if (session is null || SelectedAsset is null || !CanEdit) return;
        if (HasConflict)
        {
            ProjectStatus = "Choose Use disk or Use draft before saving this conflict.";
            return;
        }
        try
        {
            if (JsonNode.Parse(EditorText) is not JsonObject content) throw new System.Text.Json.JsonException("Asset must be a JSON object.");
            await session.SaveAsync([new(SelectedAsset.Path, content, baseHash)]);
            ++draftRevision;
            await draftGate.WaitAsync();
            try { await session.ClearDraftAsync(SelectedAsset.Path); }
            finally { draftGate.Release(); }
            IsDirty = false;
            await RefreshAsync(SelectedAsset.Path);
            ProjectStatus = "Asset saved.";
        }
        catch (ProjectConflictException)
        {
            await RefreshAsync(SelectedAsset.Path);
            ProjectStatus = "The file changed on disk. Compare both versions, then choose which to keep.";
        }
        catch (ProjectValidationException error)
        {
            ProjectStatus = string.Join("; ", error.Diagnostics.Take(3).Select(d => $"{d.File}{d.Pointer}: {d.Message}"));
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            ProjectStatus = error.Message;
        }
    }

    [RelayCommand]
    private async Task UseDiskAsync()
    {
        if (session is null || SelectedAsset is null) return;
        ++draftRevision;
        await draftGate.WaitAsync();
        try { if (!session.IsReadOnly) await session.ClearDraftAsync(SelectedAsset.Path); }
        finally { draftGate.Release(); }
        IsDirty = false;
        HasConflict = false;
        await RefreshAsync(SelectedAsset.Path);
        ProjectStatus = session.IsReadOnly ? "Disk version loaded locally; the recovery draft remains because this project is read-only." :
            "Disk version loaded; local draft discarded.";
    }

    [RelayCommand]
    private async Task UseDraftAsync()
    {
        if (session is null || SelectedAsset is null || !HasConflict || !CanEdit) return;
        ++refreshRevision;
        var latest = (await session.ReadAsync()).Assets.FirstOrDefault(a => a.Path == SelectedAsset.Path);
        if (latest is null) { ProjectStatus = "Asset was removed on disk. Copy the draft before continuing."; return; }
        baseHash = latest.Hash;
        HasConflict = false;
        ProjectStatus = "Draft selected. Save will replace the disk version shown beside it.";
    }

    private async Task RefreshAsync(string? selectedPath = null)
    {
        if (session is null) return;
        var revision = ++refreshRevision;
        var view = await session.ReadAsync();
        if (revision != refreshRevision) return;
        editingBlocked = !view.IsValid;
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(IsEditorReadOnly));
        OnPropertyChanged(nameof(CanUseDraft));
        if (editingBlocked) ProjectStatus = string.Join("; ", view.Diagnostics.Take(3).Select(d => $"{d.File}{d.Pointer}: {d.Message}"));
        selectedPath ??= SelectedAsset?.Path;
        var selected = view.Assets.FirstOrDefault(a => a.Path == selectedPath);
        if (selected is null && IsDirty)
        {
            HasConflict = true;
            DiskText = "The selected asset was removed from disk.";
            ProjectStatus = "The selected asset was removed from disk. Copy the draft before continuing.";
            return;
        }
        selected ??= view.Assets.FirstOrDefault();
        if (selected is null) return;
        if (IsDirty && baseHash is not null)
        {
            DiskText = selected.Content.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            HasConflict = selected.Hash != baseHash;
            if (HasConflict) ProjectStatus = "The disk file changed while the editor has a draft. Choose Use disk or Use draft.";
            return;
        }
        Assets = view.Assets;
        SelectedAsset = selected;
        await LoadSelectedAsync(selected);
    }

    private async Task WatchAsync(IProjectSession watched, CancellationToken token)
    {
        try
        {
            await foreach (var _ in watched.WatchAsync(token))
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
                {
                    if (session == watched) await RefreshAsync();
                }).Task.Unwrap();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    private void SetText(string value)
    {
        settingText = true;
        EditorText = value;
        settingText = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        ++draftRevision;
        await draftGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsDirty && session is { IsReadOnly: false } && currentPath is not null && baseHash is not null)
            {
                JsonObject content;
                try { content = JsonNode.Parse(EditorText) as JsonObject ?? new JsonObject(); }
                catch (System.Text.Json.JsonException) { content = new JsonObject(); }
                await session.SaveDraftAsync(new(currentPath, baseHash, content, EditorText)).ConfigureAwait(false);
            }
        }
        finally { draftGate.Release(); }
        watchCancellation?.Cancel();
        if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
        watchCancellation?.Dispose();
    }
}
