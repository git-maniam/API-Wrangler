using System.Globalization;
using System.Text.Json;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Storage;

namespace ApiWrangler.IntegrationTests;

public sealed class DatasetFileTests
{
    [Fact]
    public async Task FileImport_PublishesExactPreparedRowsAndSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "awr-file-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var session = await new ProjectRepository(Path.Combine(root, "state")).InitializeAsync(Path.Combine(root, "project"), "Fixture");
            using var reader = new StringReader("id;amount;date\n001;1,25;31/12/2026\n002;2,50;01/01/2027");
            var rows = DatasetPreview.FromDelimitedAsync(DelimitedDatasetReader.ReadAsync(reader, ';'));
            var mapped = DatasetPreview.ConvertAsync(rows, [new("id", "_caseId"), new("amount", "price", DatasetColumnType.Decimal), new("date", "date", DatasetColumnType.Date)], CultureInfo.GetCultureInfo("fr-FR"));
            await using var prepared = await PreparedDatasetFile.CreateAsync(mapped, false);
            Assert.Equal(2, prepared.RowCount);
            Assert.Contains("2026-12-31", prepared.Preview);
            Assert.False(Directory.Exists(Path.Combine(session.Root, "datasets/import")));
            var id = Guid.NewGuid();
            await prepared.CommitAsync(session, "datasets/import/dataset.json", id, "Import");
            var view = await session.ReadAsync();
            Assert.True(view.IsValid);
            var asset = view.Assets.Single(a => a.Kind == AssetKind.Dataset);
            var source = "datasets/import/" + asset.Content["source"]!["path"]!.GetValue<string>();
            var bytes = await File.ReadAllBytesAsync(Path.Combine(session.Root, source));
            Assert.Equal(prepared.Hash, CanonicalJson.Hash(bytes));
            var snapshot = await session.SnapshotAsync();
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(snapshot.Directory, source)));
            await Assert.ThrowsAsync<ProjectConflictException>(() => prepared.CommitAsync(session, asset.Path, id, "Stale"));
            Assert.Single(Directory.GetFiles(Path.Combine(session.Root, "datasets/import"), "*.jsonl"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FilePreparation_RejectsLateDuplicatesAndConversionErrors()
    {
        using var duplicate = new StringReader("{\"_caseId\":\"a\"}\n{\"_caseId\":\"a\"}");
        await Assert.ThrowsAsync<FormatException>(() => PreparedDatasetFile.CreateAsync(JsonDatasetReader.ReadLinesAsync(duplicate), false));
        using var invalid = new StringReader("{\"value\":\"1\"}\n{\"value\":\"invalid\"}");
        await Assert.ThrowsAsync<FormatException>(() => PreparedDatasetFile.CreateAsync(DatasetPreview.ConvertAsync(
            JsonDatasetReader.ReadLinesAsync(invalid), [new("value", "value", DatasetColumnType.Decimal)], CultureInfo.InvariantCulture), true));
    }

    [Fact]
    public async Task FileImport_HashFailureCleansStageAndReleasesWriterGate()
    {
        var root = Path.Combine(Path.GetTempPath(), "awr-file-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var session = await new ProjectRepository(Path.Combine(root, "state")).InitializeAsync(Path.Combine(root, "project"), "Fixture");
            using var input = new MemoryStream("{}\n"u8.ToArray());
            var definition = new System.Text.Json.Nodes.JsonObject { ["schemaVersion"] = 1, ["id"] = Guid.NewGuid().ToString(), ["name"] = "Import" };
            await Assert.ThrowsAsync<IOException>(() => session.ImportDatasetFileAsync(new("datasets/import/dataset.json", definition, null), input, "invalid"));
            Assert.Empty(Directory.GetFiles(Path.Combine(session.Root, "datasets/import")));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await session.SaveAsync([], timeout.Token);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FilePreparation_StreamsLargeInputAndBoundsPreview()
    {
        async IAsyncEnumerable<IReadOnlyDictionary<string, JsonElement>> Rows()
        {
            await Task.CompletedTask;
            for (var i = 0; i < 100_000; i++)
                yield return new Dictionary<string, JsonElement> { ["_caseId"] = JsonSerializer.SerializeToElement(i.ToString(CultureInfo.InvariantCulture)) };
        }
        await using var prepared = await PreparedDatasetFile.CreateAsync(Rows(), false);
        Assert.Equal(100_000, prepared.RowCount);
        Assert.Equal(100, prepared.Preview.Count(c => c == '\n'));
        Assert.True(prepared.Preview.Length <= 65536);
    }
}
