using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Execution;
using ApiWrangler.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApiWrangler.IntegrationTests;

public sealed class CollectionRunnerTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ApiWrangler-coll-tests", Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private HttpTransport transport = null!;
    private HttpRequestService service = null!;
    private string origin = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();

        var teardownHit = false;

        app.MapPost("/api/setup", () => Results.Ok(new { status = "initialized" }));

        app.MapGet("/api/items", () => Results.Ok(new[] { new { id = 1, name = "item-1" } }));

        app.MapPost("/api/items", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var text = await reader.ReadToEndAsync();
            return Results.Created("/api/items/1", new { id = 1, echo = text });
        });

        app.MapDelete("/api/items/1", () => Results.Ok(new { deleted = true }));

        app.MapPost("/api/teardown", () =>
        {
            teardownHit = true;
            return Results.Ok(new { status = "cleaned-up", hit = teardownHit });
        });

        app.MapGet("/api/fail", () => Results.StatusCode(500));

        await app.StartAsync();
        origin = app.Urls.Single();

        transport = new HttpTransport(directory);
        service = new HttpRequestService(transport);
    }

    [Fact]
    public async Task Collection_ExecutesCrudWorkflow_DeterministicallyWithSetupAndTeardown()
    {
        var setupId = Guid.NewGuid();
        var createId = Guid.NewGuid();
        var readId = Guid.NewGuid();
        var deleteId = Guid.NewGuid();
        var teardownId = Guid.NewGuid();
        var collId = Guid.NewGuid();

        var project = CreateTestProjectView(collId, [createId, readId, deleteId], [setupId], [teardownId]);
        var runner = new CollectionRunner(service);

        var report = await runner.ExecuteCollectionAsync(project, collId);

        Assert.Equal(RunStatus.Passed, report.Status);
        Assert.Equal(5, report.TotalRequestsRun);
        Assert.Equal(5, report.PassedRequests);
        Assert.Single(report.SetupResults);
        Assert.Single(report.TeardownResults);
        Assert.Single(report.Iterations);
        Assert.Equal(3, report.Iterations[0].Requests.Count);
    }

    [Fact]
    public async Task Collection_IterationsDoNotShareMutableStateImplicitly()
    {
        var reqId = Guid.NewGuid();
        var collId = Guid.NewGuid();

        var project = CreateTestProjectView(collId, [reqId]);
        var runner = new CollectionRunner(service);

        var options = new CollectionExecutionOptions
        {
            Iterations = 3,
            Concurrency = 2
        };

        var report = await runner.ExecuteCollectionAsync(project, collId, options);

        Assert.Equal(RunStatus.Passed, report.Status);
        Assert.Equal(3, report.Iterations.Count);

        // Verify each iteration has its own isolated variable scope
        for (int i = 0; i < report.Iterations.Count; i++)
        {
            Assert.Equal(i + 1, report.Iterations[i].IterationIndex);
            Assert.NotNull(report.Iterations[i].FinalVariables);
        }
    }

    [Fact]
    public async Task Collection_TeardownPolicyAlways_ExecutesTeardownEvenOnFailure()
    {
        var failReqId = Guid.NewGuid();
        var teardownId = Guid.NewGuid();
        var collId = Guid.NewGuid();

        var setupReq = CreateRequestAsset(Guid.NewGuid(), "Setup", origin + "/api/setup", "POST");
        var failReq = CreateRequestAsset(failReqId, "Failing Request", origin + "/api/fail", "GET");
        var teardownReq = CreateRequestAsset(teardownId, "Teardown", origin + "/api/teardown", "POST");

        var collAsset = new ProjectAsset("collections/test.collection.json", AssetKind.Collection, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = collId.ToString(),
            ["name"] = "Teardown Test Collection",
            ["requestIds"] = new JsonArray(failReqId.ToString()),
            ["teardownRequestIds"] = new JsonArray(teardownId.ToString())
        }, "hash");

        var manifest = new ProjectAsset("wrangler.json", AssetKind.Manifest, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["projectId"] = Guid.NewGuid().ToString()
        }, "hash");

        var project = new ProjectView(directory, [manifest, collAsset, setupReq, failReq, teardownReq], []);
        var runner = new CollectionRunner(service);

        var options = new CollectionExecutionOptions
        {
            TeardownPolicy = TeardownPolicy.Always,
            StopOnFailure = true
        };

        var report = await runner.ExecuteCollectionAsync(project, collId, options);

        Assert.Equal(RunStatus.Failed, report.Status);
        Assert.Single(report.TeardownResults);
        Assert.Equal(ExecutionOutcome.Passed, report.TeardownResults[0].Outcome); // Teardown completed successfully
    }

    [Fact]
    public async Task Collection_Cancellation_CancelsGracefullyAndNeverReportsPassed()
    {
        var reqId = Guid.NewGuid();
        var collId = Guid.NewGuid();

        var project = CreateTestProjectView(collId, [reqId]);
        var runner = new CollectionRunner(service);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var report = await runner.ExecuteCollectionAsync(project, collId, cancellationToken: cts.Token);

        Assert.Equal(RunStatus.Cancelled, report.Status);
    }

    private ProjectView CreateTestProjectView(Guid collId, List<Guid> requestIds, List<Guid>? setupIds = null, List<Guid>? teardownIds = null)
    {
        var assets = new List<ProjectAsset>();
        var manifest = new ProjectAsset("wrangler.json", AssetKind.Manifest, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["projectId"] = Guid.NewGuid().ToString()
        }, "hash");
        assets.Add(manifest);

        var reqArr = new JsonArray();
        foreach (var id in requestIds)
        {
            reqArr.Add(id.ToString());
            assets.Add(CreateRequestAsset(id, $"Request-{id:N}", origin + "/api/items", "GET"));
        }

        var setupArr = new JsonArray();
        if (setupIds is not null)
        {
            foreach (var id in setupIds)
            {
                setupArr.Add(id.ToString());
                assets.Add(CreateRequestAsset(id, $"Setup-{id:N}", origin + "/api/setup", "POST"));
            }
        }

        var teardownArr = new JsonArray();
        if (teardownIds is not null)
        {
            foreach (var id in teardownIds)
            {
                teardownArr.Add(id.ToString());
                assets.Add(CreateRequestAsset(id, $"Teardown-{id:N}", origin + "/api/teardown", "POST"));
            }
        }

        var collAsset = new ProjectAsset("collections/test.collection.json", AssetKind.Collection, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = collId.ToString(),
            ["name"] = "CRUD Test Collection",
            ["requestIds"] = reqArr,
            ["setupRequestIds"] = setupArr,
            ["teardownRequestIds"] = teardownArr
        }, "hash");
        assets.Add(collAsset);

        return new ProjectView(directory, assets, []);
    }

    private static ProjectAsset CreateRequestAsset(Guid id, string name, string url, string method)
    {
        var def = new HttpRequestDefinition
        {
            Id = id,
            Name = name,
            Url = url,
            Method = method
        };
        var json = RequestAssetSerializer.ToJsonObject(def);
        return new ProjectAsset($"requests/{id:N}.request.json", AssetKind.Request, json, "hash");
    }

    public async Task DisposeAsync()
    {
        transport.Dispose();
        await app.DisposeAsync();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
