namespace ApiWrangler.Domain;

public enum GridSortDirection
{
    None,
    Ascending,
    Descending
}

public sealed record DatasetGridColumn(
    string Name,
    string DataType = "string",
    bool IsPinned = false,
    bool IsKey = false);

public sealed record GridCellEdit(
    int RowIndex,
    string ColumnName,
    string? OldValue,
    string? NewValue);

public sealed record GridSortDefinition(
    string ColumnName,
    GridSortDirection Direction);

public sealed record GridFilterDefinition(
    string ColumnName,
    string Operator,
    string Value);

public sealed record ExternalResultAttachment(
    string SourceTool,
    DateTimeOffset ImportTimeUtc,
    string ContentHash,
    string? TestMapping,
    string? EnvironmentLabel,
    string ExternalStatus,
    string RawPayload)
{
    // Imported results must not be represented as runs executed by API Wrangler.
    public bool IsWranglerExecution => false;
}

public sealed record SpreadsheetExportOptions(
    bool SpreadsheetSafe = true,
    char Delimiter = ',',
    bool IncludeHeaders = true);

