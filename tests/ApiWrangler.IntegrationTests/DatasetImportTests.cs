using System.Text.Json;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Storage;

namespace ApiWrangler.IntegrationTests;

public sealed class DatasetImportTests
{
    private const string AssetPath = "datasets/import/dataset.json";

    [Fact]
    public async Task Prepare_RequiresExplicitIdsAndKeepsAcceptedPreviewImmutable()
    {
        await Assert.ThrowsAsync<FormatException>(() => DatasetImport.PrepareAsync(
            Rows("{\"value\":\"001\"}"), AssetPath, Guid.NewGuid(), "Import"));
        var prepared = await DatasetImport.PrepareAsync(Rows("{\"value\":\"001\"}"),
            AssetPath, Guid.NewGuid(), "Import", addMissingCaseIds: true);
        Assert.Equal(1, prepared.AddedCaseIds);
        var view = prepared.GetDefinition();
        var id = view["source"]!["rows"]![0]!["_caseId"]!.GetValue<string>();
        view["source"]!["rows"]![0]!["_caseId"] = "changed";
        Assert.Equal(id, prepared.GetDefinition()["source"]!["rows"]![0]!["_caseId"]!.GetValue<string>());
        Assert.Equal("001", prepared.GetDefinition()["source"]!["rows"]![0]!["value"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{\"_caseId\":\"a\"}\n{\"_caseId\":\"a\"}")]
    [InlineData("{\"_caseId\":null}")]
    [InlineData("{\"_caseId\":1}")]
    [InlineData("{\"_caseId\":\" \"}")]
    public async Task Prepare_RejectsDuplicateAndInvalidIds(string input)
    {
        await Assert.ThrowsAsync<FormatException>(() => DatasetImport.PrepareAsync(
            Rows(input), AssetPath, Guid.NewGuid(), "Import", addMissingCaseIds: true));
    }

    [Fact]
    public async Task Prepare_PreservesIdsWhenRowsReordered()
    {
        var prepared = await DatasetImport.PrepareAsync(Rows("{\"_caseId\":\"b\"}\n{\"_caseId\":\"a\"}"),
            AssetPath, Guid.NewGuid(), "Import");
        Assert.Equal(0, prepared.AddedCaseIds);
        Assert.Equal("b", prepared.GetDefinition()["source"]!["rows"]![0]!["_caseId"]!.GetValue<string>());
        Assert.Equal("a", prepared.GetDefinition()["source"]!["rows"]![1]!["_caseId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Prepare_EnforcesBoundsAndCancellation()
    {
        await Assert.ThrowsAsync<FormatException>(() => DatasetImport.PrepareAsync(
            Rows("{\"_caseId\":\"a\"}\n{\"_caseId\":\"b\"}"), AssetPath, Guid.NewGuid(), "Import", maxRows: 1));
        await Assert.ThrowsAsync<FormatException>(() => DatasetImport.PrepareAsync(
            Rows("{\"_caseId\":\"a\"}"), AssetPath, Guid.NewGuid(), "Import", maxSourceBytes: 2));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DatasetImport.PrepareAsync(
            Rows("{}"), AssetPath, Guid.NewGuid(), "Import", cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task Commit_UsesProjectTransactionAndRejectsStaleReplacement()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-dataset-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var session = await new ProjectRepository(Path.Combine(root, "state"))
                .InitializeAsync(Path.Combine(root, "project"), "Import fixture");
            var id = Guid.NewGuid();
            var initial = await DatasetImport.PrepareAsync(Rows("{\"_caseId\":\"a\",\"value\":1}"), AssetPath, id, "Import");
            Assert.False(File.Exists(Path.Combine(session.Root, AssetPath)));
            await initial.CommitAsync(session);
            var original = (await session.ReadAsync()).Assets.Single(asset => asset.Path == AssetPath);
            var stale = await DatasetImport.PrepareAsync(Rows("{\"_caseId\":\"b\"}"), AssetPath, id, "Import", original.Hash);
            var replacement = await DatasetImport.PrepareAsync(Rows("{\"_caseId\":\"a\",\"value\":2}"), AssetPath, id, "Import", original.Hash);
            await replacement.CommitAsync(session);
            await Assert.ThrowsAsync<ProjectConflictException>(() => stale.CommitAsync(session));
            var saved = (await session.ReadAsync()).Assets.Single(asset => asset.Path == AssetPath);
            Assert.Equal(2, saved.Content["source"]!["rows"]![0]!["value"]!.GetValue<int>());
            var snapshot = await session.SnapshotAsync();
            Assert.True(File.Exists(Path.Combine(snapshot.Directory, AssetPath)));
            var snapshotBytes = await File.ReadAllBytesAsync(Path.Combine(snapshot.Directory, AssetPath));
            Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(session.Root, AssetPath)), snapshotBytes);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    [Fact]
    public async Task InvalidLateRecordAndCancelledCommit_PreserveExistingAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), "ApiWrangler-dataset-tests", Guid.NewGuid().ToString("N"));
        try
        {
            await using var session = await new ProjectRepository(Path.Combine(root, "state"))
                .InitializeAsync(Path.Combine(root, "project"), "Import fixture");
            var id = Guid.NewGuid();
            var initial = await DatasetImport.PrepareAsync(Rows("{\"_caseId\":\"a\"}"), AssetPath, id, "Import");
            await initial.CommitAsync(session);
            var original = await File.ReadAllBytesAsync(Path.Combine(session.Root, AssetPath));
            await Assert.ThrowsAnyAsync<JsonException>(() => DatasetImport.PrepareAsync(
                Rows("{\"_caseId\":\"a\"}\ninvalid"), AssetPath, id, "Import"));
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initial.CommitAsync(session, cancelled.Token));
            Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(session.Root, AssetPath)));
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    private static async IAsyncEnumerable<IReadOnlyDictionary<string, JsonElement>> Rows(string jsonl)
    {
        using var reader = new StringReader(jsonl);
        await foreach (var row in JsonDatasetReader.ReadLinesAsync(reader)) { yield return row; }
    }
}
