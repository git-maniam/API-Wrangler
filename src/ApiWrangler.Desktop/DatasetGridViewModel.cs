using System.IO;
using System.Windows;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiWrangler.Desktop;

public partial class DatasetGridViewModel : ObservableObject
{
    [ObservableProperty] private EditableDatasetGrid grid = EditableDatasetGrid.FromSynthetic(100_000);
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private string statusMessage = "100,000 synthetic rows loaded. Virtualized and editable.";
    [ObservableProperty] private string clipboardSummary = "";
    [ObservableProperty] private string externalResultStatus = "";
    [ObservableProperty] private bool isSpreadsheetSafe = true;
    [ObservableProperty] private int selectedRowIndex = -1;

    public bool CanUndo => Grid.CanUndo;
    public bool CanRedo => Grid.CanRedo;

    [RelayCommand]
    private void Search()
    {
        Grid.SetSearch(SearchText);
        OnPropertyChanged(nameof(Grid));
        StatusMessage = string.IsNullOrWhiteSpace(SearchText)
            ? $"Filter cleared. Showing all {Grid.Count} rows."
            : $"Filter applied for '{SearchText}'. Showing {Grid.Count} rows.";
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = "";
        Grid.SetSearch(null);
        OnPropertyChanged(nameof(Grid));
        StatusMessage = $"Filter cleared. Showing all {Grid.Count} rows.";
    }

    [RelayCommand]
    private void Undo()
    {
        if (Grid.Undo())
        {
            OnPropertyChanged(nameof(Grid));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            StatusMessage = "Undo applied.";
        }
    }

    [RelayCommand]
    private void Redo()
    {
        if (Grid.Redo())
        {
            OnPropertyChanged(nameof(Grid));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            StatusMessage = "Redo applied.";
        }
    }

    [RelayCommand]
    private void DuplicateSelectedRow()
    {
        if (SelectedRowIndex < 0 || SelectedRowIndex >= Grid.Count)
        {
            StatusMessage = "Select a row to duplicate.";
            return;
        }

        var dup = Grid.DuplicateRow(SelectedRowIndex);
        OnPropertyChanged(nameof(Grid));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        StatusMessage = $"Duplicated case as '{dup.CaseId}'. Total rows: {Grid.TotalRowCount}.";
    }

    [RelayCommand]
    private void InsertRow()
    {
        var target = SelectedRowIndex >= 0 ? SelectedRowIndex : Grid.Count;
        Grid.InsertRow(target);
        OnPropertyChanged(nameof(Grid));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        StatusMessage = $"Inserted new row. Total rows: {Grid.TotalRowCount}.";
    }

    [RelayCommand]
    private void DeleteSelectedRow()
    {
        if (SelectedRowIndex < 0 || SelectedRowIndex >= Grid.Count)
        {
            StatusMessage = "Select a row to delete.";
            return;
        }

        if (Grid.DeleteRow(SelectedRowIndex))
        {
            OnPropertyChanged(nameof(Grid));
            OnPropertyChanged(nameof(CanUndo));
            OnPropertyChanged(nameof(CanRedo));
            StatusMessage = $"Row deleted. Remaining rows: {Grid.TotalRowCount}.";
        }
    }

    [RelayCommand]
    private void FillDown()
    {
        if (SelectedRowIndex < 0 || SelectedRowIndex >= Grid.Count - 1)
        {
            StatusMessage = "Select a row with subsequent rows to fill down.";
            return;
        }

        var targets = Enumerable.Range(SelectedRowIndex + 1, Math.Min(5, Grid.Count - SelectedRowIndex - 1)).ToList();
        var cols = Grid.Columns.Select(c => c.Name).Where(n => n != "_caseId").ToList();
        Grid.FillDown(SelectedRowIndex, targets, cols);

        OnPropertyChanged(nameof(Grid));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        StatusMessage = $"Filled down values across {targets.Count} rows.";
    }

    [RelayCommand]
    private void PasteFromClipboard()
    {
        try
        {
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text))
            {
                StatusMessage = "Clipboard is empty.";
                return;
            }

            var inspection = ClipboardFormatDetector.Inspect(text);
            ClipboardSummary = $"{inspection.Format}: {inspection.Summary}";

            if (inspection.ContainsExecutableCommand)
            {
                StatusMessage = "Pasted cURL command recognized. Shell command execution is prohibited by security policy.";
                return;
            }

            if (inspection.PreviewRows.Count > 0)
            {
                var startRow = SelectedRowIndex >= 0 ? SelectedRowIndex : 0;
                Grid.PasteGrid(startRow, Grid.Columns.Count > 1 ? Grid.Columns[1].Name : "_caseId", inspection.PreviewRows);
                OnPropertyChanged(nameof(Grid));
                OnPropertyChanged(nameof(CanUndo));
                OnPropertyChanged(nameof(CanRedo));
                StatusMessage = $"Pasted {inspection.PreviewRows.Count} rows from {inspection.Format}.";
            }
            else
            {
                StatusMessage = $"Pasted data inspection: {inspection.Summary}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Paste error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ExportSpreadsheetSafe(string? targetFilePath = null)
    {
        try
        {
            var csv = Grid.ExportToCsv(new SpreadsheetExportOptions(SpreadsheetSafe: IsSpreadsheetSafe));
            var path = targetFilePath ?? Path.Combine(Path.GetTempPath(), $"api-wrangler-export-{DateTime.UtcNow:yyyyMMddHHmmss}.csv");
            File.WriteAllText(path, csv);
            StatusMessage = IsSpreadsheetSafe
                ? $"Exported spreadsheet-safe CSV ({Grid.Count} rows) to {path}. Leading formula triggers ('=', '+', '-', '@') neutralized with single quotes."
                : $"Exported native CSV ({Grid.Count} rows) to {path}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Export error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void AttachExternalResults()
    {
        try
        {
            var text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text))
            {
                ExternalResultStatus = "Clipboard is empty. Copy external test output (e.g., JUnit XML or Newman JSON) first.";
                return;
            }

            var attachment = ExternalResultAttachmentService.CreateAttachment(text);
            var serialized = ExternalResultAttachmentService.SerializeAttachment(attachment);

            ExternalResultStatus = $"Attached external result from {attachment.SourceTool} (Hash: {attachment.ContentHash[..8]}…, Status: {attachment.ExternalStatus}, IsWranglerExecution: false).";
            StatusMessage = ExternalResultStatus;
        }
        catch (Exception ex)
        {
            ExternalResultStatus = $"Attachment error: {ex.Message}";
        }
    }
}

