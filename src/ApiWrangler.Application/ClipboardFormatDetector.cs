using System.Text.Json;
using System.Text.Json.Nodes;

namespace ApiWrangler.Application;

public enum ClipboardContentFormat
{
    Tsv,
    Csv,
    Json,
    JsonLines,
    CurlCommand,
    HttpRequest,
    StructuredObject,
    PlainText
}

public sealed record ClipboardInspectionResult(
    ClipboardContentFormat Format,
    string Summary,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> PreviewRows,
    int EstimatedRowCount,
    bool ContainsExecutableCommand = false);

public static class ClipboardFormatDetector
{
    public static ClipboardInspectionResult Inspect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new(ClipboardContentFormat.PlainText, "Empty clipboard", [], [], 0);
        }

        var trimmed = text.Trim();

        // 1. Check for cURL command
        if (trimmed.StartsWith("curl ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("curl.exe ", StringComparison.OrdinalIgnoreCase))
        {
            return new(
                ClipboardContentFormat.CurlCommand,
                "cURL command (safe non-executing preview)",
                ["Property", "Value"],
                ExtractCurlProperties(trimmed),
                1,
                ContainsExecutableCommand: true);
        }

        // 2. Check for HTTP raw request
        var httpLines = trimmed.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        if (httpLines.Length > 0 && IsHttpStartLine(httpLines[0]))
        {
            return new(
                ClipboardContentFormat.HttpRequest,
                $"HTTP Request: {httpLines[0]}",
                ["Line", "Content"],
                httpLines.Take(10).Select((l, i) => (IReadOnlyList<string>)[(i + 1).ToString(), l]).ToList(),
                httpLines.Length);
        }

        // 3. Check for JSON / Structured Object
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            try
            {
                var node = JsonNode.Parse(trimmed);
                if (node is JsonArray array)
                {
                    var (headers, rows) = ExtractJsonArrayGrid(array);
                    return new(
                        ClipboardContentFormat.Json,
                        $"JSON Array with {array.Count} records",
                        headers,
                        rows.Take(20).ToList(),
                        array.Count);
                }

                if (node is JsonObject obj)
                {
                    // Check if structured API Wrangler asset
                    var isStructured = obj.ContainsKey("schemaVersion") || obj.ContainsKey("projectId") || obj.ContainsKey("id");
                    var format = isStructured ? ClipboardContentFormat.StructuredObject : ClipboardContentFormat.Json;
                    var headers = (IReadOnlyList<string>)["Key", "Value"];
                    var rows = obj.Select(p => (IReadOnlyList<string>)[p.Key, p.Value?.ToJsonString() ?? "null"]).Take(20).ToList();
                    return new(
                        format,
                        isStructured ? "Structured API Wrangler Asset" : "JSON Object",
                        headers,
                        rows,
                        obj.Count);
                }
            }
            catch (JsonException)
            {
                // Might be JSON Lines
            }
        }

        // 4. Check for JSON Lines
        var candidateJsonLines = httpLines.Where(l => !string.IsNullOrWhiteSpace(l)).Take(5).ToList();
        if (candidateJsonLines.Count > 1 && candidateJsonLines.All(l => l.TrimStart().StartsWith('{') && l.TrimEnd().EndsWith('}')))
        {
            var validJsonLines = true;
            foreach (var l in candidateJsonLines)
            {
                try { JsonNode.Parse(l); } catch { validJsonLines = false; break; }
            }

            if (validJsonLines)
            {
                return new(
                    ClipboardContentFormat.JsonLines,
                    $"JSON Lines ({httpLines.Length} lines)",
                    ["Line", "JSON"],
                    candidateJsonLines.Select((l, i) => (IReadOnlyList<string>)[(i + 1).ToString(), l]).ToList(),
                    httpLines.Length);
            }
        }

        // 5. Check for TSV (from Excel, Google Sheets, etc.)
        var lines = httpLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        if (lines.Count > 0 && lines.Any(l => l.Contains('\t')))
        {
            var parsedTsv = lines.Select(l => l.Split('\t').Select(c => c.Trim()).ToList()).ToList();
            var maxCols = parsedTsv.Max(r => r.Count);
            if (maxCols >= 2)
            {
                var headers = parsedTsv[0];
                var dataRows = parsedTsv.Skip(1).ToList();
                return new(
                    ClipboardContentFormat.Tsv,
                    $"Spreadsheet TSV ({parsedTsv.Count} rows, {maxCols} columns)",
                    headers,
                    dataRows.Take(20).ToList(),
                    parsedTsv.Count);
            }
        }

        // 6. Check for CSV
        if (lines.Count > 0 && (lines.Any(l => l.Contains(',')) || lines.Any(l => l.Contains(';'))))
        {
            var delim = lines[0].Contains(';') && !lines[0].Contains(',') ? ';' : ',';
            var parsedCsv = lines.Select(l => ParseSimpleCsvLine(l, delim)).ToList();
            var maxCols = parsedCsv.Max(r => r.Count);
            if (maxCols >= 2)
            {
                var headers = parsedCsv[0];
                var dataRows = parsedCsv.Skip(1).ToList();
                return new(
                    ClipboardContentFormat.Csv,
                    $"Delimited CSV ({parsedCsv.Count} rows, {maxCols} columns)",
                    headers,
                    dataRows.Take(20).ToList(),
                    parsedCsv.Count);
            }
        }

        // 7. Fallback to Plain Text
        return new(
            ClipboardContentFormat.PlainText,
            $"Plain text ({lines.Count} lines)",
            ["Text"],
            lines.Take(20).Select(l => (IReadOnlyList<string>)[l]).ToList(),
            lines.Count);
    }

    private static bool IsHttpStartLine(string line)
    {
        var methods = new[] { "GET ", "POST ", "PUT ", "DELETE ", "PATCH ", "HEAD ", "OPTIONS " };
        return methods.Any(m => line.StartsWith(m, StringComparison.OrdinalIgnoreCase)) &&
               (line.Contains(" HTTP/", StringComparison.OrdinalIgnoreCase) || line.Contains(" /"));
    }

    private static IReadOnlyList<IReadOnlyList<string>> ExtractCurlProperties(string curlCommand)
    {
        var result = new List<IReadOnlyList<string>>
        {
            new[] { "Command Type", "cURL shell command (EXECUTION PROHIBITED)" }
        };

        // Extract method if -X or --request is present
        var methodIdx = curlCommand.IndexOf("-X ", StringComparison.OrdinalIgnoreCase);
        if (methodIdx >= 0)
        {
            var parts = curlCommand[(methodIdx + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) result.Add(["HTTP Method", parts[0].Trim('\'', '"')]);
        }
        else if (curlCommand.Contains("--data", StringComparison.OrdinalIgnoreCase) || curlCommand.Contains("-d ", StringComparison.OrdinalIgnoreCase))
        {
            result.Add(["HTTP Method", "POST (inferred from data argument)"]);
        }
        else
        {
            result.Add(["HTTP Method", "GET (default)"]);
        }

        // Extract URL candidate
        var tokens = curlCommand.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var url = tokens.FirstOrDefault(t => t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("'http", StringComparison.OrdinalIgnoreCase) || t.StartsWith("\"http", StringComparison.OrdinalIgnoreCase));
        if (url is not null)
        {
            result.Add(["Target URL", url.Trim('\'', '"')]);
        }

        return result;
    }

    private static (IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows) ExtractJsonArrayGrid(JsonArray array)
    {
        if (array.Count == 0) return ([], []);

        var headers = new HashSet<string>(StringComparer.Ordinal);
        var rowObjects = new List<JsonObject>();

        foreach (var item in array)
        {
            if (item is JsonObject obj)
            {
                rowObjects.Add(obj);
                foreach (var prop in obj) headers.Add(prop.Key);
            }
        }

        var headerList = headers.ToList();
        var rows = new List<IReadOnlyList<string>>();

        foreach (var obj in rowObjects)
        {
            var row = new List<string>();
            foreach (var h in headerList)
            {
                row.Add(obj.TryGetPropertyValue(h, out var v) && v is not null ? v.ToString() : string.Empty);
            }
            rows.Add(row);
        }

        return (headerList, rows);
    }

    private static IReadOnlyList<string> ParseSimpleCsvLine(string line, char delimiter)
    {
        var cells = new List<string>();
        var inQuotes = false;
        var current = new System.Text.StringBuilder();

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
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
            else if (c == delimiter && !inQuotes)
            {
                cells.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        cells.Add(current.ToString().Trim());
        return cells;
    }
}
