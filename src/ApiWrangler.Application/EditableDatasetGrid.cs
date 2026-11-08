using System.Collections;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed record BulkReplaceMatch(int RowIndex, string ColumnName, string OldValue, string NewValue);

public sealed class GridRowData
{
    public int OriginalIndex { get; set; }
    public string CaseId { get; set; }
    public Dictionary<string, string?> Cells { get; }

    public GridRowData(int originalIndex, string caseId, IDictionary<string, string?>? initialCells = null)
    {
        OriginalIndex = originalIndex;
        CaseId = caseId;
        Cells = initialCells != null ? new Dictionary<string, string?>(initialCells, StringComparer.Ordinal) : new Dictionary<string, string?>(StringComparer.Ordinal);
        Cells["_caseId"] = caseId;
    }

    public string? GetValue(string column) => Cells.TryGetValue(column, out var val) ? val : null;
    public void SetValue(string column, string? val)
    {
        Cells[column] = val;
        if (column == "_caseId" && !string.IsNullOrWhiteSpace(val))
        {
            CaseId = val;
        }
    }
}

public sealed class EditableDatasetGrid : IList
{
    private const int PageSize = 256;
    private readonly List<DatasetGridColumn> columns = [];
    private readonly List<GridRowData> underlyingRows = [];
    private readonly List<GridRowData> projectedView = [];

    private readonly Stack<IReadOnlyList<GridCellEdit>> undoStack = [];
    private readonly Stack<IReadOnlyList<GridCellEdit>> redoStack = [];

    private GridSortDefinition? currentSort;
    private string? currentSearch;

    public IReadOnlyList<DatasetGridColumn> Columns => columns.AsReadOnly();
    public int TotalRowCount => underlyingRows.Count;
    public int Count => projectedView.Count;
    public bool CanUndo => undoStack.Count > 0;
    public bool CanRedo => redoStack.Count > 0;

    public bool IsReadOnly => false;
    public bool IsFixedSize => false;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public EditableDatasetGrid(int initialRowCount = 0)
    {
        columns.Add(new DatasetGridColumn("_caseId", "string", IsPinned: true, IsKey: true));
        columns.Add(new DatasetGridColumn("email", "string"));
        columns.Add(new DatasetGridColumn("status", "number"));

        for (var i = 0; i < initialRowCount; i++)
        {
            var row = new GridRowData(i, $"case-{i:D6}", new Dictionary<string, string?>
            {
                ["_caseId"] = $"case-{i:D6}",
                ["email"] = $"user{i}@example.test",
                ["status"] = "200"
            });
            underlyingRows.Add(row);
        }

        RebuildView();
    }

    public static EditableDatasetGrid FromSynthetic(int rowCount = 100_000)
    {
        return new EditableDatasetGrid(rowCount);
    }

    public static EditableDatasetGrid FromData(IReadOnlyList<string> columnNames, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var grid = new EditableDatasetGrid(0);
        grid.columns.Clear();
        foreach (var c in columnNames)
        {
            grid.columns.Add(new DatasetGridColumn(c, "string", IsPinned: c == "_caseId", IsKey: c == "_caseId"));
        }

        var idx = 0;
        foreach (var r in rows)
        {
            var dict = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var c = 0; c < columnNames.Count; c++)
            {
                dict[columnNames[c]] = c < r.Count ? r[c] : null;
            }

            var caseId = dict.TryGetValue("_caseId", out var cid) && !string.IsNullOrWhiteSpace(cid)
                ? cid
                : $"case-{idx:D6}";

            grid.underlyingRows.Add(new GridRowData(idx, caseId, dict));
            idx++;
        }

        grid.RebuildView();
        return grid;
    }

    public object? this[int index]
    {
        get
        {
            if (index < 0 || index >= projectedView.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return projectedView[index];
        }
        set => throw new NotSupportedException("Use EditCell or SetCellValue to modify grid rows.");
    }

    public GridRowData GetRow(int viewIndex)
    {
        if (viewIndex < 0 || viewIndex >= projectedView.Count) throw new ArgumentOutOfRangeException(nameof(viewIndex));
        return projectedView[viewIndex];
    }

    public void AddColumn(string name, string type = "string", bool isPinned = false)
    {
        if (columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) return;
        columns.Add(new DatasetGridColumn(name, type, isPinned));
    }

    public void EditCell(int viewIndex, string columnName, string? newValue)
    {
        var row = GetRow(viewIndex);
        var oldValue = row.GetValue(columnName);
        if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) return;

        row.SetValue(columnName, newValue);
        undoStack.Push([new GridCellEdit(row.OriginalIndex, columnName, oldValue, newValue)]);
        redoStack.Clear();

        if (currentSearch is not null || currentSort is not null)
        {
            RebuildView();
        }
    }

    public void ApplyEdits(IReadOnlyList<GridCellEdit> edits)
    {
        if (edits.Count == 0) return;

        var executed = new List<GridCellEdit>();
        foreach (var edit in edits)
        {
            if (edit.RowIndex < 0 || edit.RowIndex >= underlyingRows.Count) continue;
            var row = underlyingRows[edit.RowIndex];
            var old = row.GetValue(edit.ColumnName);
            row.SetValue(edit.ColumnName, edit.NewValue);
            executed.Add(new GridCellEdit(edit.RowIndex, edit.ColumnName, old, edit.NewValue));
        }

        undoStack.Push(executed);
        redoStack.Clear();
        RebuildView();
    }

    public bool Undo()
    {
        if (undoStack.Count == 0) return false;

        var edits = undoStack.Pop();
        foreach (var edit in edits)
        {
            if (edit.RowIndex < 0 || edit.RowIndex >= underlyingRows.Count) continue;
            var row = underlyingRows[edit.RowIndex];
            row.SetValue(edit.ColumnName, edit.OldValue);
        }

        redoStack.Push(edits);
        RebuildView();
        return true;
    }

    public bool Redo()
    {
        if (redoStack.Count == 0) return false;

        var edits = redoStack.Pop();
        foreach (var edit in edits)
        {
            if (edit.RowIndex < 0 || edit.RowIndex >= underlyingRows.Count) continue;
            var row = underlyingRows[edit.RowIndex];
            row.SetValue(edit.ColumnName, edit.NewValue);
        }

        undoStack.Push(edits);
        RebuildView();
        return true;
    }

    public void FillDown(int sourceViewIndex, IEnumerable<int> targetViewIndices, IEnumerable<string> columnNames)
    {
        var sourceRow = GetRow(sourceViewIndex);
        var edits = new List<GridCellEdit>();

        foreach (var targetIdx in targetViewIndices)
        {
            if (targetIdx == sourceViewIndex || targetIdx < 0 || targetIdx >= projectedView.Count) continue;
            var targetRow = projectedView[targetIdx];

            foreach (var col in columnNames)
            {
                if (col == "_caseId") continue; // Never duplicate unique case IDs across rows
                edits.Add(new GridCellEdit(targetRow.OriginalIndex, col, targetRow.GetValue(col), sourceRow.GetValue(col)));
            }
        }

        ApplyEdits(edits);
    }

    public GridRowData DuplicateRow(int viewIndex)
    {
        var sourceRow = GetRow(viewIndex);
        var newIndex = underlyingRows.Count;
        var newCaseId = $"case-{Guid.NewGuid():N}"[..12];

        var clonedValues = new Dictionary<string, string?>(sourceRow.Cells, StringComparer.Ordinal)
        {
            ["_caseId"] = newCaseId
        };

        var newRow = new GridRowData(newIndex, newCaseId, clonedValues);
        underlyingRows.Add(newRow);
        RebuildView();
        return newRow;
    }

    public void InsertRow(int atViewIndex, IDictionary<string, string?>? initialValues = null)
    {
        var targetIndex = atViewIndex >= 0 && atViewIndex <= projectedView.Count ? atViewIndex : projectedView.Count;
        var newCaseId = initialValues != null && initialValues.TryGetValue("_caseId", out var cid) && !string.IsNullOrWhiteSpace(cid)
            ? cid
            : $"case-{Guid.NewGuid():N}"[..12];

        var row = new GridRowData(underlyingRows.Count, newCaseId, initialValues);
        underlyingRows.Add(row);
        RebuildView();
    }

    public bool DeleteRow(int viewIndex)
    {
        if (viewIndex < 0 || viewIndex >= projectedView.Count) return false;
        var row = projectedView[viewIndex];
        underlyingRows.Remove(row);
        // Re-index remaining rows
        for (var i = 0; i < underlyingRows.Count; i++)
        {
            underlyingRows[i].OriginalIndex = i;
        }
        RebuildView();
        return true;
    }

    public void PasteGrid(int startViewRow, string startColumn, IReadOnlyList<IReadOnlyList<string>> pasteData)
    {
        if (pasteData.Count == 0 || startViewRow < 0) return;

        var startColIdx = columns.FindIndex(c => string.Equals(c.Name, startColumn, StringComparison.OrdinalIgnoreCase));
        if (startColIdx < 0) startColIdx = 0;

        var edits = new List<GridCellEdit>();

        for (var r = 0; r < pasteData.Count; r++)
        {
            var targetRowIdx = startViewRow + r;
            if (targetRowIdx >= projectedView.Count)
            {
                // Auto-expand rows if needed
                InsertRow(projectedView.Count);
            }

            var row = projectedView[targetRowIdx];
            var pasteRow = pasteData[r];

            for (var c = 0; c < pasteRow.Count; c++)
            {
                var targetColIdx = startColIdx + c;
                if (targetColIdx >= columns.Count)
                {
                    // Add column if beyond bounds
                    var autoColName = $"col_{targetColIdx + 1}";
                    AddColumn(autoColName);
                }

                var colName = columns[targetColIdx].Name;
                var oldVal = row.GetValue(colName);
                var newVal = pasteRow[c];

                edits.Add(new GridCellEdit(row.OriginalIndex, colName, oldVal, newVal));
            }
        }

        ApplyEdits(edits);
    }

    public void SetSort(string columnName, GridSortDirection direction)
    {
        if (direction == GridSortDirection.None)
        {
            currentSort = null;
        }
        else
        {
            currentSort = new GridSortDefinition(columnName, direction);
        }
        RebuildView();
    }

    public void SetSearch(string? filterText)
    {
        currentSearch = string.IsNullOrWhiteSpace(filterText) ? null : filterText.Trim();
        RebuildView();
    }

    public IReadOnlyList<BulkReplaceMatch> PreviewBulkReplace(string findText, string replaceText, string? specificColumn = null)
    {
        var matches = new List<BulkReplaceMatch>();
        if (string.IsNullOrEmpty(findText)) return matches;

        var cols = specificColumn != null
            ? columns.Where(c => string.Equals(c.Name, specificColumn, StringComparison.OrdinalIgnoreCase)).ToList()
            : columns;

        foreach (var row in projectedView)
        {
            foreach (var col in cols)
            {
                var val = row.GetValue(col.Name);
                if (val is not null && val.Contains(findText, StringComparison.OrdinalIgnoreCase))
                {
                    var replaced = val.Replace(findText, replaceText, StringComparison.OrdinalIgnoreCase);
                    matches.Add(new BulkReplaceMatch(row.OriginalIndex, col.Name, val, replaced));
                }
            }
        }

        return matches;
    }

    public void ApplyBulkReplace(IReadOnlyList<BulkReplaceMatch> matches)
    {
        var edits = matches.Select(m => new GridCellEdit(m.RowIndex, m.ColumnName, m.OldValue, m.NewValue)).ToList();
        ApplyEdits(edits);
    }

    public string ExportToCsv(SpreadsheetExportOptions? options = null)
    {
        var opt = options ?? new SpreadsheetExportOptions();
        var headers = columns.Select(c => c.Name).ToList();
        var rows = projectedView.Select(r => (IReadOnlyList<string?>)columns.Select(c => r.GetValue(c.Name)).ToList());
        return SpreadsheetSafeExport.ToCsv(headers, rows, opt);
    }

    private void RebuildView()
    {
        projectedView.Clear();

        IEnumerable<GridRowData> filtered = underlyingRows;

        if (!string.IsNullOrEmpty(currentSearch))
        {
            filtered = filtered.Where(r => r.Cells.Values.Any(v => v is not null && v.Contains(currentSearch, StringComparison.OrdinalIgnoreCase)));
        }

        if (currentSort is not null && currentSort.Direction != GridSortDirection.None)
        {
            var col = currentSort.ColumnName;
            filtered = currentSort.Direction == GridSortDirection.Ascending
                ? filtered.OrderBy(r => r.GetValue(col) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                : filtered.OrderByDescending(r => r.GetValue(col) ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        }

        projectedView.AddRange(filtered);
    }

    // IList implementation
    public int IndexOf(object? value) => value is GridRowData row ? projectedView.IndexOf(row) : -1;
    public bool Contains(object? value) => IndexOf(value) >= 0;
    public IEnumerator GetEnumerator() => projectedView.GetEnumerator();
    public void CopyTo(Array array, int index)
    {
        for (var i = 0; i < projectedView.Count; i++)
        {
            array.SetValue(projectedView[i], index + i);
        }
    }

    public int Add(object? value)
    {
        if (value is GridRowData row)
        {
            underlyingRows.Add(row);
            RebuildView();
            return projectedView.IndexOf(row);
        }
        throw new ArgumentException("Value must be GridRowData", nameof(value));
    }

    public void Clear()
    {
        underlyingRows.Clear();
        undoStack.Clear();
        redoStack.Clear();
        RebuildView();
    }

    public void Insert(int index, object? value)
    {
        if (value is GridRowData row)
        {
            underlyingRows.Insert(index, row);
            RebuildView();
        }
    }

    public void Remove(object? value)
    {
        if (value is GridRowData row)
        {
            underlyingRows.Remove(row);
            RebuildView();
        }
    }

    public void RemoveAt(int index) => DeleteRow(index);
}
