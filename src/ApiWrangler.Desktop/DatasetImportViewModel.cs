using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiWrangler.Desktop;

public partial class DatasetImportViewModel(Func<IProjectSession?> getSession) : ObservableObject
{
    [ObservableProperty] private string sourcePath = "";
    [ObservableProperty] private string datasetName = "Imported dataset";
    [ObservableProperty] private string format = "CSV";
    [ObservableProperty] private string encodingName = "UTF-8";
    [ObservableProperty] private string delimiter = ",";
    [ObservableProperty] private string cultureName = "";
    [ObservableProperty] private string mappings = "[]";
    [ObservableProperty] private bool addCaseIds;
    [ObservableProperty] private string previewText = "";
    [ObservableProperty] private string previewHighlighting = "CSV";
    [ObservableProperty] private string status = "Choose a source, prepare a preview, then accept the import.";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle))] private bool isBusy;
    public bool IsIdle => !IsBusy;
    public string[] Formats { get; } = ["CSV", "TSV", "JSON", "JSONL"];
    public string[] Encodings { get; } = ["UTF-8", "UTF-16 LE", "Windows-1252"];
    private PreparedDatasetFile? prepared;
    private IProjectSession? preparedSession;
    private string preparedOptions = "";
    private string targetPath = "";
    private string preparedName = "";
    private Guid datasetId;
    private string Options => JsonSerializer.Serialize(new { SourcePath, DatasetName, Format, EncodingName, Delimiter, CultureName, Mappings, AddCaseIds });

    partial void OnFormatChanged(string value)
    {
        PreviewHighlighting = DatasetPreviewFormatting.DetectHighlighting(value);
    }

    [RelayCommand]
    private void Browse()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Dataset files|*.csv;*.tsv;*.json;*.jsonl|All files|*.*" };
        if (dialog.ShowDialog() == true) SourcePath = dialog.FileName;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task PrepareAsync(CancellationToken token)
    {
        if (IsBusy) return;
        var session = getSession();
        if (session is null || session.IsReadOnly) { Status = "Open a writable project first."; return; }
        IsBusy = true;
        try
        {
            if (prepared is not null) { await prepared.DisposeAsync(); prepared = null; }
            PreviewText = "";
            var options = Options;
            var path = SourcePath;
            var name = DatasetName;
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            var selectedFormat = Format;
            var selectedEncoding = EncodingName;
            var separator = Format == "TSV" ? '\t' : Delimiter.Length == 1 ? Delimiter[0] : throw new FormatException("Delimiter must be one character.");
            var culture = CultureInfo.GetCultureInfo(CultureName);
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            jsonOptions.Converters.Add(new JsonStringEnumConverter());
            var columns = JsonSerializer.Deserialize<DatasetColumnMapping[]>(Mappings, jsonOptions) ?? throw new FormatException("Mappings must be an array.");
            var addIds = AddCaseIds;
            var view = await session.ReadAsync(token);
            var datasetDirectory = view.Assets.Single(a => a.Kind == AssetKind.Manifest).Content["paths"]!["datasets"]!.GetValue<string>();
            prepared = await Task.Run(async () =>
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                var encoding = selectedEncoding switch
                {
                    "UTF-8" => new UTF8Encoding(false, true),
                    "UTF-16 LE" => (Encoding)new UnicodeEncoding(false, true, true),
                    _ => Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
                };
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                using var reader = new StreamReader(input, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
                await using var utf8 = Encoding.CreateTranscodingStream(input, encoding, new UTF8Encoding(false, true), leaveOpen: true);
                var rows = selectedFormat switch
                {
                    "JSON" => JsonDatasetReader.ReadArrayAsync(utf8, token),
                    "JSONL" => JsonDatasetReader.ReadLinesAsync(reader, cancellationToken: token),
                    _ => DatasetPreview.FromDelimitedAsync(DelimitedDatasetReader.ReadAsync(reader, separator, cancellationToken: token), token)
                };
                return await PreparedDatasetFile.CreateAsync(DatasetPreview.ConvertAsync(rows, columns, culture, token), addIds, token);
            }, token);
            preparedOptions = options;
            preparedSession = session;
            preparedName = name;
            datasetId = Guid.NewGuid();
            targetPath = $"{datasetDirectory}/{datasetId:D}/dataset.json";
            PreviewText = prepared.Preview;
            Status = $"Prepared {prepared.RowCount:N0} rows; {prepared.AddedCaseIds:N0} case IDs added. Preview shows up to 100 rows / 64 KiB. Accept saves this exact prepared data.";
        }
        catch (OperationCanceledException) { Status = "Preparation cancelled; project unchanged."; }
        catch (Exception error) when (error is IOException or ArgumentException or FormatException or JsonException or ProjectValidationException)
        { Status = $"Preparation failed ({error.GetType().Name}); check source format, case IDs and mappings. Project unchanged."; }
        finally { IsBusy = false; }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task AcceptAsync(CancellationToken token)
    {
        if (IsBusy) return;
        if (prepared is null || preparedOptions != Options || !ReferenceEquals(preparedSession, getSession()))
        { Status = "Prepare a fresh preview for the current project and options."; return; }
        IsBusy = true;
        try
        {
            await prepared.CommitAsync(preparedSession!, targetPath, datasetId, preparedName, token: token);
            Status = $"Imported {prepared.RowCount:N0} rows into {targetPath}.";
            await prepared.DisposeAsync();
            prepared = null;
        }
        catch (OperationCanceledException) { Status = "Import cancelled; check project status before retrying."; }
        catch (Exception error) when (error is IOException or ArgumentException or ProjectValidationException)
        { Status = $"Import failed ({error.GetType().Name}); previous datasets preserved."; }
        finally { IsBusy = false; }
    }
}
