using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public interface IProjectRepository
{
    Task<IProjectSession> InitializeAsync(string root, string name, CancellationToken token = default);
    Task<IProjectSession> OpenAsync(string path, bool writable = true, CancellationToken token = default);
}

public interface IProjectSession : IAsyncDisposable
{
    string Root { get; }
    bool IsReadOnly { get; }
    Task<ProjectView> ReadAsync(CancellationToken token = default);
    Task SaveAsync(IReadOnlyList<AssetEdit> changes, CancellationToken token = default);
    Task<SnapshotInfo> SnapshotAsync(CancellationToken token = default);
    // Each coalesced notification, including watcher overflow, requires a full project rescan.
    IAsyncEnumerable<bool> WatchAsync(CancellationToken token = default);
    Task SaveDraftAsync(EditorDraft draft, CancellationToken token = default);
    Task<EditorDraft?> ReadDraftAsync(string path, CancellationToken token = default);
    Task ClearDraftAsync(string path, CancellationToken token = default);
}
