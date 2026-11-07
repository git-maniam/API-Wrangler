using System.Text.Json.Nodes;

namespace ApiWrangler.Domain;

public enum AssetKind { Manifest, Collection, Folder, Request, Environment, Dataset }
public sealed record ProjectDiagnostic(string File, string Pointer, string Message, string? EntityId = null);
public sealed record ProjectAsset(string Path, AssetKind Kind, JsonObject Content, string Hash)
{
    public string Name => Content["name"] is JsonValue value && value.TryGetValue<string>(out var name) ? name : Path;
    public string? Id => (Content[Kind == AssetKind.Manifest ? "projectId" : "id"] as JsonValue)?.ToString();
}
public sealed record ProjectView(string Root, IReadOnlyList<ProjectAsset> Assets, IReadOnlyList<ProjectDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.Count == 0;
}
public sealed record AssetEdit(string Path, JsonObject Content, string? ExpectedHash);
public sealed record SnapshotInfo(string Id, string Directory, int FileCount, string? Commit, bool? Dirty);
public sealed record EditorDraft(string Path, string BaseHash, JsonObject Content, string? Text = null);

public sealed class ProjectValidationException(IReadOnlyList<ProjectDiagnostic> diagnostics)
    : Exception("Project validation failed. Inspect the file and field diagnostics.")
{
    public IReadOnlyList<ProjectDiagnostic> Diagnostics { get; } = diagnostics;
}
public sealed class ProjectConflictException(string path)
    : IOException($"The file changed on disk: {path}. Reload or resolve the conflict before saving.");
public sealed class ProjectReadOnlyException() : IOException("The project is read-only or another writer owns it.");
