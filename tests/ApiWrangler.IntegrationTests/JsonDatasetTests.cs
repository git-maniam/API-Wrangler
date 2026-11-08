using System.Text;
using System.Text.Json;
using ApiWrangler.Application;

namespace ApiWrangler.IntegrationTests;

public sealed class JsonDatasetTests
{
    [Fact]
    public async Task ReadArray_RejectsOversizedRecordBeforeReadingWholeSource()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[{\"value\":\"" + new string('a', 100_000) + "\"}]"));
        await Assert.ThrowsAsync<FormatException>(async () =>
        {
            await foreach (var row in JsonDatasetReader.ReadArrayAsync(stream, maxRecordBytes: 100)) { }
        });
        Assert.True(stream.Position <= 4096);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public async Task ReadArray_BoundResetsPerRecordAndIgnoresDelimitersInsideStrings()
    {
        const string record = "{\"nested\":[{\"text\":\"comma,bracket]quote\\\"slash\\\\\"}]}";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat(record, 1000)) + "]"));
        var count = 0;
        await foreach (var row in JsonDatasetReader.ReadArrayAsync(stream, maxRecordBytes: Encoding.UTF8.GetByteCount(record)))
        {
            count++;
            Assert.Equal(JsonValueKind.Array, row["nested"].ValueKind);
        }
        Assert.Equal(1000, count);
    }

    [Fact]
    public async Task ReadArray_CountsUtf8BytesAndHonorsExactLimit()
    {
        const string record = "{\"value\":\"é漢\"}";
        var bytes = Encoding.UTF8.GetBytes("[" + record + "]");
        using var accepted = new MemoryStream(bytes);
        var count = 0;
        await foreach (var row in JsonDatasetReader.ReadArrayAsync(accepted, maxRecordBytes: bytes.Length - 2)) { count++; }
        Assert.Equal(1, count);
        using var rejected = new MemoryStream(bytes);
        await Assert.ThrowsAsync<FormatException>(async () =>
        {
            await foreach (var row in JsonDatasetReader.ReadArrayAsync(rejected, maxRecordBytes: bytes.Length - 3)) { }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_PreservesTypesNestingMissingAndNull(bool lines)
    {
        const string first = "{\"id\":\"001\",\"null\":null,\"number\":1.25,\"flag\":true,\"nested\":{\"items\":[1,\"two\"]}}";
        var rows = await ReadAsync(lines ? first + "\r\n{}\n" : "[" + first + ",{}]", lines);
        Assert.Equal(2, rows.Count);
        Assert.Equal("001", rows[0]["id"].GetString());
        Assert.Equal(JsonValueKind.Null, rows[0]["null"].ValueKind);
        Assert.False(rows[1].ContainsKey("null"));
        Assert.Equal(1.25m, rows[0]["number"].GetDecimal());
        Assert.True(rows[0]["flag"].GetBoolean());
        Assert.Equal("two", rows[0]["nested"].GetProperty("items")[1].GetString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("[]")]
    [InlineData("{\"x\":1,\"x\":2}")]
    [InlineData("{\"nested\":[{\"x\":1,\"x\":2}]}")]
    public async Task Read_RejectsNonObjectsAndDuplicateProperties(string record)
    {
        await Assert.ThrowsAsync<FormatException>(() => ReadAsync("[" + record + "]", false));
        await Assert.ThrowsAsync<FormatException>(() => ReadAsync(record, true));
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("[{},]", false)]
    [InlineData("[{}] extra", false)]
    [InlineData("{}\n\n{}", true)]
    [InlineData("{\"x\":\n1}", true)]
    public async Task Read_RejectsMalformedInput(string input, bool lines)
    {
        await Assert.ThrowsAnyAsync<JsonException>(() => ReadAsync(input, lines));
    }

    [Fact]
    public async Task ReadLines_HandlesBomBufferBoundaryAndFinalRecord()
    {
        var value = new string('a', 5000);
        var rows = await ReadAsync("\uFEFF{\"value\":\"" + value + "\"}\r\n{}", true);
        Assert.Equal(value, rows[0]["value"].GetString());
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task ReadLines_RejectsOversizeBeforeParsing()
    {
        using var reader = new StringReader("{\"value\":\"" + new string('a', 100) + "\"}");
        await Assert.ThrowsAsync<FormatException>(async () =>
        {
            await foreach (var row in JsonDatasetReader.ReadLinesAsync(reader, 20)) { }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Read_ObservesCancellation(bool lines)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadAsync(
            lines ? "{}" : "[{}]", lines, cancellation.Token));
    }

    private static async Task<List<IReadOnlyDictionary<string, JsonElement>>> ReadAsync(
        string input, bool lines, CancellationToken token = default)
    {
        using var reader = new StringReader(input);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var source = lines ? JsonDatasetReader.ReadLinesAsync(reader, cancellationToken: token)
            : JsonDatasetReader.ReadArrayAsync(stream, token);
        var rows = new List<IReadOnlyDictionary<string, JsonElement>>();
        await foreach (var row in source) { rows.Add(row); }
        return rows;
    }
}
