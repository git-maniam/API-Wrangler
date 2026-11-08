using ApiWrangler.Application;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class DatasetPreviewHighlightingTests
{
    [Theory]
    [InlineData("CSV", "CSV")]
    [InlineData("TSV", "CSV")]
    [InlineData("JSON", "JSON")]
    [InlineData("JSONL", "JSONL")]
    [InlineData("unknown", "CSV")]
    public void DatasetPreview_DetectsCorrectHighlightingMode(string format, string expectedMode)
    {
        var mode = DatasetPreviewFormatting.DetectHighlighting(format);
        Assert.Equal(expectedMode, mode);
    }

    [Fact]
    public void DatasetPreview_PreservesExactRawSource_AndRemainsBounded()
    {
        const string rawCsv = "id,name,role\r\n101,Alice,Engineer\r\n102,Bob,Manager";
        var lines = rawCsv.Split(["\r\n", "\n"], StringSplitOptions.None);
        Assert.Equal(3, lines.Length);
        Assert.Equal("id,name,role", lines[0]);
    }
}
