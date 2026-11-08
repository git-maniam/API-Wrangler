using System.Text;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public static class SpreadsheetSafeExport
{
    private static readonly char[] FormulaTriggers = ['=', '+', '-', '@', '\t', '\r'];

    public static string EscapeCell(string? value, bool spreadsheetSafe = true)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var cell = value;
        if (spreadsheetSafe && FormulaTriggers.Contains(cell[0]))
        {
            // Neutralize formula injection risk by prefixing with single quote
            cell = "'" + cell;
        }

        // Quote if cell contains quotes, commas, tabs, semicolons, or newlines
        if (cell.Contains('"') || cell.Contains(',') || cell.Contains('\t') || cell.Contains(';') || cell.Contains('\n') || cell.Contains('\r'))
        {
            cell = "\"" + cell.Replace("\"", "\"\"") + "\"";
        }

        return cell;
    }

    public static string ToCsv(
        IReadOnlyList<string> headers,
        IEnumerable<IReadOnlyList<string?>> rows,
        SpreadsheetExportOptions? options = null)
    {
        var opt = options ?? new SpreadsheetExportOptions();
        var sb = new StringBuilder();

        if (opt.IncludeHeaders && headers.Count > 0)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                if (i > 0) sb.Append(opt.Delimiter);
                sb.Append(EscapeCell(headers[i], opt.SpreadsheetSafe));
            }
            sb.AppendLine();
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < headers.Count; i++)
            {
                if (i > 0) sb.Append(opt.Delimiter);
                var val = i < row.Count ? row[i] : string.Empty;
                sb.Append(EscapeCell(val, opt.SpreadsheetSafe));
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }
}

