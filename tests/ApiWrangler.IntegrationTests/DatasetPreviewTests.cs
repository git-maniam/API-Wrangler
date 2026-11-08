using System.Globalization;
using System.Text.Json;
using ApiWrangler.Application;

namespace ApiWrangler.IntegrationTests;

public sealed class DatasetPreviewTests
{
    [Fact]
    public async Task Preview_MapsExplicitConversionsWithoutChangingRawValues()
    {
        using var reader = new StringReader("id;amount;flag;empty\n001;1,25;true;\n002;2,50;false;\n");
        var source = DatasetPreview.FromDelimitedAsync(DelimitedDatasetReader.ReadAsync(reader, ';'));
        var preview = await DatasetPreview.CreateAsync(source,
            [new("id", "case"), new("amount", "price", DatasetColumnType.Decimal),
             new("flag", "enabled", DatasetColumnType.Boolean), new("empty", "null", EmptyStringAsNull: true),
             new("empty", "empty"), new("absent", "missing")], CultureInfo.GetCultureInfo("fr-FR"));
        Assert.False(preview.HasMore);
        Assert.Equal("001", preview.Rows[0]["case"].GetString());
        Assert.Equal(1.25m, preview.Rows[0]["price"].GetDecimal());
        Assert.True(preview.Rows[0]["enabled"].GetBoolean());
        Assert.Equal(JsonValueKind.Null, preview.Rows[0]["null"].ValueKind);
        Assert.Equal("", preview.Rows[0]["empty"].GetString());
        Assert.False(preview.Rows[0].ContainsKey("missing"));
    }

    [Fact]
    public async Task Preview_BoundsRowsAndDoesNotEnumerateEntireSource()
    {
        var consumed = 0;
        async IAsyncEnumerable<IReadOnlyDictionary<string, JsonElement>> Source()
        {
            await Task.CompletedTask;
            for (var i = 0; i < 100_000; i++)
            {
                consumed++;
                yield return new Dictionary<string, JsonElement>();
            }
        }
        var result = await DatasetPreview.CreateAsync(Source(), [], CultureInfo.InvariantCulture, 10);
        Assert.True(result.HasMore);
        Assert.Equal(10, result.Rows.Count);
        Assert.Equal(11, consumed);
    }

    [Fact]
    public async Task Preview_RejectsDuplicateTargetsAndRedactsConversionFailures()
    {
        using var reader = new StringReader("{\"value\":\"secret-value\"}");
        await Assert.ThrowsAsync<ArgumentException>(() => DatasetPreview.CreateAsync(
            JsonDatasetReader.ReadLinesAsync(reader), [new("a", "same"), new("b", "same")], CultureInfo.InvariantCulture));
        var error = await Assert.ThrowsAsync<FormatException>(() => DatasetPreview.CreateAsync(
            JsonDatasetReader.ReadLinesAsync(reader), [new("value", "number", DatasetColumnType.Decimal)], CultureInfo.InvariantCulture));
        Assert.Contains("row 1", error.Message);
        Assert.DoesNotContain("secret-value", error.ToString());
    }

    [Fact]
    public async Task Preview_PreservesNestedValuesAndMissingFieldsByDefault()
    {
        using var reader = new StringReader("{\"nested\":{\"x\":[1,null]},\"empty\":null}\n{}");
        var result = await DatasetPreview.CreateAsync(JsonDatasetReader.ReadLinesAsync(reader), [], CultureInfo.InvariantCulture);
        Assert.Equal(JsonValueKind.Null, result.Rows[0]["nested"].GetProperty("x")[1].ValueKind);
        Assert.Empty(result.Rows[1]);
    }
}
