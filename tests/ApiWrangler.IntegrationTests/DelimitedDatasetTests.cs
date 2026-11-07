using ApiWrangler.Application;

namespace ApiWrangler.IntegrationTests;

public sealed class DelimitedDatasetTests
{
    [Theory]
    [InlineData(',')]
    [InlineData('\t')]
    [InlineData(';')]
    public async Task Read_PreservesQuotedMultilineAndRawValues(char delimiter)
    {
        using var reader = new StringReader($"\uFEFFid{delimiter}text{delimiter}empty\r\n001{delimiter}\"a{delimiter}b\r\nc\"\"d\"{delimiter}\r\n");
        var rows = await ReadAllAsync(reader, delimiter);
        var row = Assert.Single(rows);
        Assert.Equal("001", row["id"]);
        Assert.Equal($"a{delimiter}b\r\nc\"d", row["text"]);
        Assert.Equal("", row["empty"]);
    }

    [Theory]
    [InlineData("id,id\n1,2")]
    [InlineData("id,\n1,2")]
    [InlineData("id,name\n1")]
    [InlineData("id\n\"unfinished")]
    [InlineData("id\n\"value\"suffix")]
    [InlineData("id\na\"b")]
    public async Task Read_RejectsAmbiguousOrMalformedRecords(string input)
    {
        using var reader = new StringReader(input);
        await Assert.ThrowsAsync<FormatException>(() => ReadAllAsync(reader));
    }

    [Fact]
    public async Task Read_PreservesQuotesAcrossBufferBoundaryAndFinalEmptyField()
    {
        var text = new string('x', 4090) + "\"tail";
        using var reader = new StringReader("a,b\n\"" + text.Replace("\"", "\"\"") + "\",");
        var row = Assert.Single(await ReadAllAsync(reader));
        Assert.Equal(text, row["a"]);
        Assert.Equal("", row["b"]);
    }

    [Fact]
    public async Task Read_EnforcesRecordBoundAndCancellation()
    {
        using var reader = new StringReader("id\n12345");
        await Assert.ThrowsAsync<FormatException>(async () =>
        {
            await foreach (var row in DelimitedDatasetReader.ReadAsync(reader, maxRecordCharacters: 4)) { }
        });
        using var cancelledReader = new StringReader("id\n1");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var row in DelimitedDatasetReader.ReadAsync(cancelledReader, cancellationToken: cancellation.Token)) { }
        });
    }

    private static async Task<List<IReadOnlyDictionary<string, string>>> ReadAllAsync(TextReader reader, char delimiter = ',')
    {
        var rows = new List<IReadOnlyDictionary<string, string>>();
        await foreach (var row in DelimitedDatasetReader.ReadAsync(reader, delimiter)) { rows.Add(row); }
        return rows;
    }
}
