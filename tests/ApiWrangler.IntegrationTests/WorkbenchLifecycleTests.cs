using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Storage;

namespace ApiWrangler.IntegrationTests;

public sealed class WorkbenchLifecycleTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ApiWrangler-workbench-tests", Guid.NewGuid().ToString("N"));
    private readonly string stateDirectory = Path.Combine(Path.GetTempPath(), "ApiWrangler-workbench-state", Guid.NewGuid().ToString("N"));
    private ProjectRepository repository = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(stateDirectory);
        repository = new ProjectRepository(stateDirectory);
        await using var session = await repository.InitializeAsync(directory, "Workbench Lifecycle Test");
    }

    [Fact]
    public void RequestAssetSerializer_ProducesSchemaCompliantV1Asset()
    {
        var request = new HttpRequestDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Get Users",
            Description = "Fetch active users list",
            Method = "GET",
            Url = "https://api.example.test/users",
            TimeoutMs = 15_000,
            Headers =
            [
                new("Accept", "application/json"),
                new("X-Custom", "value1"),
                new("X-Custom", "value2")
            ],
            Query =
            [
                new("page", "1"),
                new("tag", "alpha"),
                new("tag", "beta")
            ],
            BodyDefinition = new RequestBodyDefinition
            {
                Mode = "raw",
                Text = "{\"filter\":\"active\"}"
            }
        };

        var json = RequestAssetSerializer.ToJsonObject(request);
        Assert.Equal(1, json["schemaVersion"]?.GetValue<int>());
        Assert.Equal(request.Id.ToString(), json["id"]?.GetValue<string>());
        Assert.Equal("Get Users", json["name"]?.GetValue<string>());
        Assert.Equal("GET", json["method"]?.GetValue<string>());
        Assert.Equal("https://api.example.test/users", json["url"]?.GetValue<string>());

        var headers = json["headers"]!.AsArray();
        Assert.Equal(3, headers.Count);

        var query = json["query"]!.AsArray();
        Assert.Equal(3, query.Count);

        var validator = new AssetValidator();
        var asset = new ProjectAsset("collections/default/requests/users.request.json", AssetKind.Request, json, "dummy");
        var manifest = new ProjectAsset("wrangler.json", AssetKind.Manifest, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["projectId"] = Guid.NewGuid().ToString(),
            ["name"] = "Test",
            ["paths"] = new JsonObject
            {
                ["collections"] = "collections",
                ["environments"] = "environments",
                ["datasets"] = "datasets"
            }
        }, "dummy");

        var diagnostics = validator.Validate(directory, [manifest, asset]);
        Assert.Empty(diagnostics);

        // Test deserialization roundtrip
        var deserialized = RequestAssetSerializer.FromJsonObject(json);
        Assert.Equal(request.Id, deserialized.Id);
        Assert.Equal(request.Name, deserialized.Name);
        Assert.Equal(request.Method, deserialized.Method);
        Assert.Equal(request.Url, deserialized.Url);
        Assert.Equal(3, deserialized.Headers.Count);
        Assert.Equal(3, deserialized.Query.Count);
        Assert.Equal("{\"filter\":\"active\"}", deserialized.BodyDefinition?.Text);
    }

    [Fact]
    public async Task Workbench_CreateSaveCloseReopenRerun_SucceedsInProjectSession()
    {
        await using var session = await repository.OpenAsync(directory);
        var initialView = await session.ReadAsync();
        var manifest = initialView.Assets.Single(a => a.Kind == AssetKind.Manifest);
        var collectionsDir = manifest.Content["paths"]!["collections"]!.GetValue<string>();

        // 1. Create request definition
        var request = new HttpRequestDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Echo Endpoint",
            Method = "POST",
            Url = "http://127.0.0.1:5057/echo",
            Headers = [new("Content-Type", "application/json"), new("X-Client", "API-Wrangler")],
            Query = [new("v", "1"), new("v", "2")],
            BodyDefinition = new RequestBodyDefinition { Mode = "raw", Text = "{\"saved\":true}" }
        };

        // 2. Save into project
        var path = $"{collectionsDir}/requests/echo.request.json";
        var content = RequestAssetSerializer.ToJsonObject(request);
        await session.SaveAsync([new AssetEdit(path, content, null)]);

        // 3. Close and reopen project session
        await using var reopenedSession = await repository.OpenAsync(directory);
        var view = await reopenedSession.ReadAsync();
        var savedAsset = view.Assets.Single(a => a.Path == path);
        Assert.Equal(AssetKind.Request, savedAsset.Kind);

        // 4. Reopen/deserialize request
        var loaded = RequestAssetSerializer.FromJsonObject(savedAsset.Content);
        Assert.Equal(request.Id, loaded.Id);
        Assert.Equal("Echo Endpoint", loaded.Name);
        Assert.Equal("POST", loaded.Method);
        Assert.Equal("http://127.0.0.1:5057/echo", loaded.Url);
        Assert.Equal(2, loaded.Headers.Count);
        Assert.Equal(2, loaded.Query.Count);
        Assert.Equal("{\"saved\":true}", loaded.BodyDefinition?.Text);
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        if (Directory.Exists(stateDirectory)) Directory.Delete(stateDirectory, true);
    }
}

