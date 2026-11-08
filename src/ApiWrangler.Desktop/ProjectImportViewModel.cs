using System.IO;
using System.Text.Json;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiWrangler.Desktop;

public partial class ProjectImportViewModel(Func<IProjectSession?> getSession) : ObservableObject
{
    [ObservableProperty] private string sourcePath = "";
    [ObservableProperty] private string sourceText = "";
    [ObservableProperty] private ImportSourceFormat selectedFormat = ImportSourceFormat.AutoDetect;
    [ObservableProperty] private string previewSummary = "";
    [ObservableProperty] private string entityCountsText = "";
    [ObservableProperty] private string detectedSecretsText = "";
    [ObservableProperty] private string unsupportedFeaturesText = "";
    [ObservableProperty] private string status = "Paste content or choose a file, then preview the import.";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool isBusy;
    [ObservableProperty] private bool canPublish;

    public bool IsIdle => !IsBusy;
    public ImportSourceFormat[] Formats { get; } =
    [
        ImportSourceFormat.AutoDetect,
        ImportSourceFormat.PostmanCollection,
        ImportSourceFormat.PostmanEnvironment,
        ImportSourceFormat.OpenApi,
        ImportSourceFormat.Curl,
        ImportSourceFormat.Har
    ];

    private ImportPreview? currentPreview;

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Importable files|*.json;*.yaml;*.yml;*.har;*.txt;*.sh|All files|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            SourcePath = dialog.FileName;
            SourceText = File.ReadAllText(dialog.FileName);
            Preview();
        }
    }

    [RelayCommand]
    public void Preview()
    {
        if (string.IsNullOrWhiteSpace(SourceText) && !string.IsNullOrWhiteSpace(SourcePath) && File.Exists(SourcePath))
        {
            SourceText = File.ReadAllText(SourcePath);
        }

        if (string.IsNullOrWhiteSpace(SourceText))
        {
            Status = "Provide source text or select a valid file to import.";
            CanPublish = false;
            return;
        }

        currentPreview = ProjectImportService.Preview(SourceText, SelectedFormat);

        if (!currentPreview.IsValid)
        {
            Status = $"Validation error: {string.Join("; ", currentPreview.ValidationErrors)}";
            CanPublish = false;
            PreviewSummary = string.Join("\n", currentPreview.ValidationErrors);
            return;
        }

        EntityCountsText = $"Requests: {currentPreview.Counts.Requests}, Folders: {currentPreview.Counts.Folders}, Environments: {currentPreview.Counts.Environments}, Variables: {currentPreview.Counts.Variables}";

        if (currentPreview.DetectedSecrets.Count > 0)
        {
            DetectedSecretsText = string.Join("\n", currentPreview.DetectedSecrets.Select(s => $"[!] {s.Key}: {s.Reason} -> Recommended storage: {s.RecommendedStorage}"));
        }
        else
        {
            DetectedSecretsText = "No sensitive tokens detected.";
        }

        if (currentPreview.UnsupportedFeatures.Count > 0)
        {
            UnsupportedFeaturesText = string.Join("\n", currentPreview.UnsupportedFeatures.Select(f => $"[-] {f}"));
        }
        else
        {
            UnsupportedFeaturesText = "All features are supported.";
        }

        PreviewSummary = $"Detected format: {currentPreview.DetectedFormat}\nProposed files ({currentPreview.ProposedAssets.Count}):\n" +
                         string.Join("\n", currentPreview.ProposedAssets.Select(a => $"  + {a.Path} ({a.Kind}: {a.Name})"));

        CanPublish = true;
        Status = $"Preview ready ({currentPreview.ProposedAssets.Count} assets to create). Click 'Publish' to import.";
    }

    [RelayCommand(IncludeCancelCommand = true)]
    public async Task PublishAsync(CancellationToken token)
    {
        if (IsBusy || currentPreview == null || !currentPreview.IsValid) return;

        var session = getSession();
        if (session is null || session.IsReadOnly)
        {
            Status = "Open a writable project before importing.";
            return;
        }

        IsBusy = true;
        Status = "Publishing import transaction...";
        try
        {
            ImportReport report = await ProjectImportService.PublishAsync(session, currentPreview, token).ConfigureAwait(true);
            Status = $"Successfully imported {report.Counts.Requests} requests into project ({report.WrittenAssetPaths.Count} files written). Report saved to {report.ReportFilePath}.";
            CanPublish = false;
            currentPreview = null;
        }
        catch (OperationCanceledException)
        {
            Status = "Import cancelled. Project files were not modified.";
        }
        catch (Exception ex)
        {
            Status = $"Import failed: {ex.Message}. Previous project assets were preserved.";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
