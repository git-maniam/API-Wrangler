using System.Diagnostics;
using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class DatasetGridAndClipboardTests
{
    [Fact]
    public void EditableDatasetGrid_100kRows_IsResponsive_AndRetainsEdits()
    {
        var sw = Stopwatch.StartNew();
        var grid = EditableDatasetGrid.FromSynthetic(100_000);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 500, $"Grid initialization took {sw.ElapsedMilliseconds} ms, expected < 500 ms.");
        Assert.Equal(100_000, grid.TotalRowCount);
        Assert.Equal(100_000, grid.Count);

        // Responsive cell retrieval
        sw.Restart();
        var row42 = grid.GetRow(42);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 10, $"Row retrieval took {sw.ElapsedMilliseconds} ms.");
        Assert.Equal("case-000042", row42.CaseId);
        Assert.Equal("user42@example.test", row42.GetValue("email"));

        // Responsive editing
        sw.Restart();
        grid.EditCell(42, "email", "custom_tester_42@domain.test");
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 50, $"Cell edit took {sw.ElapsedMilliseconds} ms.");
        Assert.Equal("custom_tester_42@domain.test", grid.GetRow(42).GetValue("email"));
        Assert.True(grid.CanUndo);

        // Undo
        Assert.True(grid.Undo());
        Assert.Equal("user42@example.test", grid.GetRow(42).GetValue("email"));
        Assert.True(grid.CanRedo);

        // Redo
        Assert.True(grid.Redo());
        Assert.Equal("custom_tester_42@domain.test", grid.GetRow(42).GetValue("email"));
    }

    [Fact]
    public void EditableDatasetGrid_FillDown_And_DuplicateRow()
    {
        var grid = EditableDatasetGrid.FromSynthetic(10);
        grid.EditCell(0, "email", "shared_template@corp.internal");

        // Fill down to rows 1, 2, 3
        grid.FillDown(0, [1, 2, 3], ["email"]);
        Assert.Equal("shared_template@corp.internal", grid.GetRow(1).GetValue("email"));
        Assert.Equal("shared_template@corp.internal", grid.GetRow(2).GetValue("email"));
        Assert.Equal("shared_template@corp.internal", grid.GetRow(3).GetValue("email"));

        // Case IDs must not be duplicated
        Assert.NotEqual(grid.GetRow(0).CaseId, grid.GetRow(1).CaseId);
        Assert.NotEqual(grid.GetRow(1).CaseId, grid.GetRow(2).CaseId);

        // Duplicate case
        var dup = grid.DuplicateRow(0);
        Assert.Equal(11, grid.TotalRowCount);
        Assert.StartsWith("case-", dup.CaseId);
        Assert.NotEqual(grid.GetRow(0).CaseId, dup.CaseId);
        Assert.Equal("shared_template@corp.internal", dup.GetValue("email"));
    }

    [Fact]
    public void EditableDatasetGrid_SortingAndFiltering_DoesNotMutateSourceOrder()
    {
        var grid = EditableDatasetGrid.FromSynthetic(10);

        // Verify original order
        Assert.Equal("case-000000", grid.GetRow(0).CaseId);
        Assert.Equal("case-000009", grid.GetRow(9).CaseId);

        // Sort descending by caseId
        grid.SetSort("_caseId", GridSortDirection.Descending);
        Assert.Equal("case-000009", grid.GetRow(0).CaseId);
        Assert.Equal("case-000000", grid.GetRow(9).CaseId);

        // Underlying source row index must not have been permanently reordered
        Assert.Equal(0, grid.GetRow(9).OriginalIndex);
        Assert.Equal(9, grid.GetRow(0).OriginalIndex);

        // Clear sort
        grid.SetSort("_caseId", GridSortDirection.None);
        Assert.Equal("case-000000", grid.GetRow(0).CaseId);

        // Filter / Search
        grid.SetSearch("user5@");
        Assert.Single(grid);
        Assert.Equal("case-000005", grid.GetRow(0).CaseId);

        // Clear filter
        grid.SetSearch(null);
        Assert.Equal(10, grid.Count);
    }

    [Fact]
    public void ClipboardFormatDetector_RecognizesAllFormats_AndRejectsCommandExecution()
    {
        // 1. Spreadsheet TSV
        const string tsv = "id\tname\tscore\r\n1\tAlice\t98\r\n2\tBob\t85";
        var tsvRes = ClipboardFormatDetector.Inspect(tsv);
        Assert.Equal(ClipboardContentFormat.Tsv, tsvRes.Format);
        Assert.Equal(3, tsvRes.Headers.Count);
        Assert.Equal(2, tsvRes.PreviewRows.Count);
        Assert.False(tsvRes.ContainsExecutableCommand);

        // 2. CSV
        const string csv = "sku,price,inStock\r\nITEM-A,19.99,true\r\nITEM-B,4.50,false";
        var csvRes = ClipboardFormatDetector.Inspect(csv);
        Assert.Equal(ClipboardContentFormat.Csv, csvRes.Format);
        Assert.Equal(3, csvRes.Headers.Count);
        Assert.Equal(2, csvRes.PreviewRows.Count);

        // 3. JSON Array
        const string jsonArray = "[{\"orderId\": \"ORD-1\", \"total\": 100}, {\"orderId\": \"ORD-2\", \"total\": 250}]";
        var jsonRes = ClipboardFormatDetector.Inspect(jsonArray);
        Assert.Equal(ClipboardContentFormat.Json, jsonRes.Format);
        Assert.Equal(2, jsonRes.PreviewRows.Count);

        // 4. cURL command (Execution strictly prohibited)
        const string curl = "curl -X POST https://api.example.com/v1/orders -H 'Content-Type: application/json' -d '{\"item\":\"book\"}'";
        var curlRes = ClipboardFormatDetector.Inspect(curl);
        Assert.Equal(ClipboardContentFormat.CurlCommand, curlRes.Format);
        Assert.True(curlRes.ContainsExecutableCommand);
        Assert.Contains(curlRes.PreviewRows, r => r[0] == "HTTP Method" && r[1].Contains("POST"));
        Assert.Contains(curlRes.PreviewRows, r => r[0] == "Target URL" && r[1] == "https://api.example.com/v1/orders");

        // 5. Raw HTTP Request
        const string httpReq = "GET /v1/users HTTP/1.1\r\nHost: api.example.com\r\nAccept: application/json";
        var httpRes = ClipboardFormatDetector.Inspect(httpReq);
        Assert.Equal(ClipboardContentFormat.HttpRequest, httpRes.Format);
    }

    [Fact]
    public void SpreadsheetSafeExport_FormulaNeutralization()
    {
        var headers = new[] { "Name", "FormulaTest", "NormalValue" };
        var rows = new List<IReadOnlyList<string?>>
        {
            new[] { "Admin", "=SUM(A1:A10)", "42" },
            new[] { "Manager", "+CMD|' /C calc'!A0", "Finance" },
            new[] { "Guest", "-100", "Balance" },
            new[] { "Lead", "@SUM(B1:B2)", "Active" },
            new[] { "SafeUser", "Normal text", "123" }
        };

        // Spreadsheet-safe export
        var safeCsv = SpreadsheetSafeExport.ToCsv(headers, rows, new SpreadsheetExportOptions(SpreadsheetSafe: true));

        // Formulas starting with =, +, -, @ must be neutralized with leading single quote
        Assert.Contains("'=SUM(A1:A10)", safeCsv);
        Assert.Contains("'+CMD|' /C calc'!A0", safeCsv);
        Assert.Contains("'-100", safeCsv);
        Assert.Contains("'@SUM(B1:B2)", safeCsv);
        Assert.Contains("Normal text", safeCsv);

        // Native export without formula escaping
        var nativeCsv = SpreadsheetSafeExport.ToCsv(headers, rows, new SpreadsheetExportOptions(SpreadsheetSafe: false));
        Assert.Contains("=SUM(A1:A10)", nativeCsv);
        Assert.DoesNotContain("'=SUM(A1:A10)", nativeCsv);
    }

    [Fact]
    public void ExternalResultAttachment_ProvenanceIntegrity()
    {
        const string newmanOutput = "{\"collection\": {\"name\": \"Regression Suite\"}, \"run\": {\"failures\": 0}}";

        var attachment = ExternalResultAttachmentService.CreateAttachment(
            newmanOutput,
            specifiedSourceTool: "Newman",
            testMapping: "Collection-Checkout",
            environmentLabel: "Staging");

        Assert.Equal("Newman", attachment.SourceTool);
        Assert.False(attachment.IsWranglerExecution);
        Assert.Equal("Passed", attachment.ExternalStatus);
        Assert.Equal("Collection-Checkout", attachment.TestMapping);
        Assert.Equal("Staging", attachment.EnvironmentLabel);

        var json = ExternalResultAttachmentService.SerializeAttachment(attachment);
        var parsed = JsonNode.Parse(json)!;

        Assert.False(parsed["isWranglerExecution"]!.GetValue<bool>());
        Assert.Equal("Newman", parsed["provenance"]!["sourceTool"]!.GetValue<string>());
        Assert.Equal("Passed", parsed["provenance"]!["externalStatus"]!.GetValue<string>());
        Assert.Equal(attachment.ContentHash, parsed["provenance"]!["contentHash"]!.GetValue<string>());
    }
}
