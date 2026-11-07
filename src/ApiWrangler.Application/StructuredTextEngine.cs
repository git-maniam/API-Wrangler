using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ApiWrangler.Application;

public enum StructuredFormat
{
    Raw,
    Json,
    Xml,
    Yaml,
    GraphQL,
    Csv
}

public sealed record BeautifyResult(
    bool Success,
    string Text,
    string? Diagnostic,
    StructuredFormat Format);

public static class StructuredTextEngine
{
    public const int DefaultBeautifyLimitBytes = 5 * 1024 * 1024; // 5 MiB

    public static StructuredFormat DetectFormat(string? text, string? contentType = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return StructuredFormat.Raw;

        var ct = contentType?.ToLowerInvariant() ?? "";
        if (ct.Contains("json")) return StructuredFormat.Json;
        if (ct.Contains("xml")) return StructuredFormat.Xml;
        if (ct.Contains("yaml") || ct.Contains("yml")) return StructuredFormat.Yaml;
        if (ct.Contains("graphql")) return StructuredFormat.GraphQL;
        if (ct.Contains("csv")) return StructuredFormat.Csv;

        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return StructuredFormat.Json;

        if (trimmed.StartsWith('<') && trimmed.EndsWith('>'))
            return StructuredFormat.Xml;

        if (trimmed.StartsWith("query ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("mutation ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("subscription ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("fragment ", StringComparison.OrdinalIgnoreCase))
            return StructuredFormat.GraphQL;

        if (trimmed.Contains(':') && (trimmed.StartsWith("---") || trimmed.Contains("\n  ") || trimmed.Contains("\n- ")))
            return StructuredFormat.Yaml;

        if (text.Contains(',') && text.Contains('\n'))
        {
            var firstLine = text.Split('\n').FirstOrDefault()?.Trim();
            if (firstLine is not null && firstLine.Split(',').Length >= 2)
                return StructuredFormat.Csv;
        }

        return StructuredFormat.Raw;
    }

    public static BeautifyResult Beautify(string? text, string? contentType = null, int maxSizeBytes = DefaultBeautifyLimitBytes)
    {
        if (string.IsNullOrEmpty(text))
            return new BeautifyResult(true, "", null, StructuredFormat.Raw);

        if (Encoding.UTF8.GetByteCount(text) > maxSizeBytes)
        {
            return new BeautifyResult(
                Success: false,
                Text: text,
                Diagnostic: $"Content exceeds the beautification limit of {maxSizeBytes / (1024 * 1024)} MiB. Showing raw content.",
                Format: DetectFormat(text, contentType));
        }

        var format = DetectFormat(text, contentType);
        return format switch
        {
            StructuredFormat.Json => FormatJson(text),
            StructuredFormat.Xml => FormatXml(text),
            StructuredFormat.Yaml => FormatYaml(text),
            StructuredFormat.GraphQL => FormatGraphQL(text),
            StructuredFormat.Csv => FormatCsv(text),
            _ => new BeautifyResult(true, text, null, StructuredFormat.Raw)
        };
    }

    public static BeautifyResult FormatJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            using var stream = new MemoryStream();
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            doc.WriteTo(writer);
            writer.Flush();
            var formatted = Encoding.UTF8.GetString(stream.ToArray());
            return new BeautifyResult(true, formatted, null, StructuredFormat.Json);
        }
        catch (JsonException ex)
        {
            var line = ex.LineNumber.HasValue ? $"Line {ex.LineNumber.Value + 1}" : "Unknown line";
            var pos = ex.BytePositionInLine.HasValue ? $"Column {ex.BytePositionInLine.Value + 1}" : "";
            var loc = string.IsNullOrEmpty(pos) ? line : $"{line}, {pos}";
            var message = $"JSON parse error at {loc}: {ex.Message}";
            return new BeautifyResult(false, text, message, StructuredFormat.Json);
        }
    }

    public static BeautifyResult FormatXml(string text)
    {
        try
        {
            var doc = XDocument.Parse(text);
            var formatted = doc.ToString(SaveOptions.None);
            return new BeautifyResult(true, formatted, null, StructuredFormat.Xml);
        }
        catch (System.Xml.XmlException ex)
        {
            var message = $"XML parse error at Line {ex.LineNumber}, Column {ex.LinePosition}: {ex.Message}";
            return new BeautifyResult(false, text, message, StructuredFormat.Xml);
        }
    }

    public static BeautifyResult FormatYaml(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains('\t'))
            {
                return new BeautifyResult(false, text, $"YAML formatting error at line {i + 1}: Tabs are not permitted in YAML indentation.", StructuredFormat.Yaml);
            }
            sb.AppendLine(line);
        }

        return new BeautifyResult(true, sb.ToString().TrimEnd(), null, StructuredFormat.Yaml);
    }

    public static BeautifyResult FormatGraphQL(string text)
    {
        int braceDepth = 0;
        int parenDepth = 0;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith('}') || line.StartsWith(')'))
            {
                braceDepth = Math.Max(0, braceDepth - (line.Count(c => c == '}') - line.Count(c => c == '{')));
                parenDepth = Math.Max(0, parenDepth - (line.Count(c => c == ')') - line.Count(c => c == '(')));
            }

            var indent = new string(' ', Math.Max(0, braceDepth) * 2);
            sb.AppendLine(indent + line);

            if (!line.StartsWith('}') && !line.StartsWith(')'))
            {
                braceDepth += line.Count(c => c == '{') - line.Count(c => c == '}');
                parenDepth += line.Count(c => c == '(') - line.Count(c => c == ')');
            }
        }

        if (braceDepth != 0 || parenDepth != 0)
        {
            return new BeautifyResult(false, text, "GraphQL syntax error: Unbalanced braces or parentheses.", StructuredFormat.GraphQL);
        }

        return new BeautifyResult(true, sb.ToString().TrimEnd(), null, StructuredFormat.GraphQL);
    }

    public static BeautifyResult FormatCsv(string text)
    {
        var rawLines = text.Replace("\r\n", "\n").Split('\n');
        var parsedRows = new List<List<string>>();
        string? diagnostic = null;

        foreach (var line in rawLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = ParseCsvLine(line, out var rowDiag);
            if (rowDiag is not null)
            {
                diagnostic ??= rowDiag;
            }
            parsedRows.Add(cells);
        }

        if (parsedRows.Count == 0)
            return new BeautifyResult(true, text, null, StructuredFormat.Csv);

        if (diagnostic is not null)
            return new BeautifyResult(false, text, diagnostic, StructuredFormat.Csv);

        int colCount = parsedRows.Max(r => r.Count);
        var colWidths = new int[colCount];

        foreach (var row in parsedRows)
        {
            for (int c = 0; c < row.Count; c++)
            {
                colWidths[c] = Math.Max(colWidths[c], row[c].Length);
            }
        }

        var sb = new StringBuilder();
        for (int r = 0; r < parsedRows.Count; r++)
        {
            var row = parsedRows[r];
            for (int c = 0; c < colCount; c++)
            {
                var val = c < row.Count ? row[c] : "";
                sb.Append(val.PadRight(colWidths[c] + 2));
            }
            sb.AppendLine();
        }

        return new BeautifyResult(true, sb.ToString().TrimEnd(), null, StructuredFormat.Csv);
    }

    private static List<string> ParseCsvLine(string line, out string? diagnostic)
    {
        diagnostic = null;
        var cells = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (inQuotes)
        {
            diagnostic = "CSV error: Unterminated quoted field in row.";
        }

        cells.Add(current.ToString());
        return cells;
    }
}
