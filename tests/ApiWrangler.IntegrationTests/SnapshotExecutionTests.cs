using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Execution;
using ApiWrangler.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class SnapshotExecutionTests : IAsyncLifetime
{
    private readonly string testDir = Path.Combine(Path.GetTempPath(), "ApiWrangler-snap-tests-" + Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private HttpTransport transport = null!;
    private HttpRequestService service = null!;
    private string origin = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(testDir);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();

        app.MapGet("/api/original", () => Results.Ok(new { version = "original" }));
        app.MapGet("/api/modified", () => Results.Ok(new { version = "modified" }));

        await app.StartAsync();
        origin = app.Urls.Single();

        transport = new HttpTransport(testDir);
        service = new HttpRequestService(transport);
    }

    [Fact]
    public async Task SnapshotExecution_IsolateFromLaterProjectEdits()
    {
        var repo = new ProjectRepository();
        var projDir = Path.Combine(testDir, "isolated-project");
        await using var session = await repo.InitializeAsync(projDir, "Snapshot Isolation Project");

        var collAsset = (await session.ReadAsync()).Assets.First(a => a.Kind == AssetKind.Collection);
        var collId = Guid.Parse(collAsset.Id!);
        var reqAsset = (await session.ReadAsync()).Assets.First(a => a.Kind == AssetKind.Request);

        // Update the request to target /api/original
        var reqJson = reqAsset.Content.DeepClone().AsObject();
        reqJson["url"] = origin + "/api/original";
        await session.SaveAsync([new AssetEdit(reqAsset.Path, reqJson, reqAsset.Hash)]);

        // 1. Capture execution snapshot
        var snapshot = await session.SnapshotAsync();
        Assert.NotNull(snapshot.Id);
        Assert.True(Directory.Exists(snapshot.Directory));

        // 2. Modify the file on disk AFTER snapshot was taken
        var modifiedJson = reqJson.DeepClone().AsObject();
        modifiedJson["url"] = origin + "/api/modified";
        var currentReqAsset = (await session.ReadAsync()).Assets.First(a => a.Kind == AssetKind.Request);
        await session.SaveAsync([new AssetEdit(reqAsset.Path, modifiedJson, currentReqAsset.Hash)]);

        // Verify disk file changed
        var diskView = await session.ReadAsync();
        Assert.Contains("/api/modified", diskView.Assets.First(a => a.Kind == AssetKind.Request).Content["url"]!.ToString());

        // 3. Execute collection against the SNAPSHOT
        await using var snapSession = await repo.OpenAsync(snapshot.Directory, writable: false);
        var snapView = await snapSession.ReadAsync();

        var runner = new CollectionRunner(service);
        var report = await runner.ExecuteCollectionAsync(snapView, collId, snapshot: snapshot);

        Assert.Equal(RunStatus.Passed, report.Status);
        Assert.Equal(snapshot.Id, report.SnapshotHash);

        // Verify request executed against /api/original from the snapshot, NOT /api/modified
        var executedReq = report.Iterations[0].Requests[0];
        Assert.Contains("original", executedReq.ResponsePreview);
        Assert.DoesNotContain("modified", executedReq.ResponsePreview);
    }

    [Fact]
    public async Task SnapshotExecution_WithDirtyDraft_IncludesDraftWithoutOverwritingDiskAsset()
    {
        var repo = new ProjectRepository();
        var projDir = Path.Combine(testDir, "draft-project");
        await using var session = await repo.InitializeAsync(projDir, "Draft Project");

        var view = await session.ReadAsync();
        var reqAsset = view.Assets.First(a => a.Kind == AssetKind.Request);

        // Prepare an unsaved in-memory editor draft with a modified name and target
        var draftJson = reqAsset.Content.DeepClone().AsObject();
        draftJson["name"] = "In-Memory Draft Request";
        draftJson["url"] = origin + "/api/original";

        var draft = new EditorDraft(reqAsset.Path, reqAsset.Hash, draftJson);

        // Create snapshot with dirty draft included
        var snapshot = await session.SnapshotAsync([draft]);
        Assert.True(snapshot.Dirty);

        // 1. Verify working project file on disk was NOT overwritten
        var diskView = await session.ReadAsync();
        var diskReq = diskView.Assets.First(a => a.Kind == AssetKind.Request);
        Assert.NotEqual("In-Memory Draft Request", diskReq.Name);

        // 2. Verify snapshot directory contains the draft content
        await using var snapSession = await repo.OpenAsync(snapshot.Directory, writable: false);
        var snapView = await snapSession.ReadAsync();
        var snapReq = snapView.Assets.First(a => a.Kind == AssetKind.Request);
        Assert.Equal("In-Memory Draft Request", snapReq.Name);

        // 3. Verify execution against snapshot executes the draft
        var collAsset = snapView.Assets.First(a => a.Kind == AssetKind.Collection);
        var collId = Guid.Parse(collAsset.Id!);

        var runner = new CollectionRunner(service);
        var report = await runner.ExecuteCollectionAsync(snapView, collId, snapshot: snapshot);

        Assert.Equal(RunStatus.Passed, report.Status);
        Assert.True(report.IsDirty);
        Assert.Equal(snapshot.Id, report.SnapshotHash);
        Assert.Equal("In-Memory Draft Request", report.Iterations[0].Requests[0].RequestName);
    }

    public async Task DisposeAsync()
    {
        transport.Dispose();
        await app.DisposeAsync();
        if (Directory.Exists(testDir))
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
    }
}

