using System.Text.Json;

namespace ApiWrangler.Storage;

public sealed record LocalSettings(
    string? LastProject = null,
    string EditorFontFamily = "Segoe UI",
    double EditorFontSize = 16.0);

public sealed class LocalSettingsStore
{
    private readonly string file;
    public LocalSettingsStore(string? directory = null) => file = Path.Combine(directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApiWrangler", "settings"), "desktop.json");

    public async Task<LocalSettings> ReadAsync(CancellationToken token = default)
    {
        if (!File.Exists(file)) return new();
        try { return JsonSerializer.Deserialize<LocalSettings>(await File.ReadAllBytesAsync(file, token).ConfigureAwait(false)) ?? new(); }
        catch (JsonException) { throw new IOException("Local settings are invalid. Preserve the settings file before resetting it."); }
    }

    public Task SaveAsync(LocalSettings settings, CancellationToken token = default) =>
        ProjectSession.WriteStateAsync(file, JsonSerializer.SerializeToUtf8Bytes(settings), token);
}
