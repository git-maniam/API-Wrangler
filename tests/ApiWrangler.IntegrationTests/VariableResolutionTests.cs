using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Storage;

namespace ApiWrangler.IntegrationTests;

public sealed class VariableResolutionTests : IAsyncLifetime
{
    private readonly string secretDir = Path.Combine(Path.GetTempPath(), "ApiWrangler-secret-tests", Guid.NewGuid().ToString("N"));
    private LocalSecretStore secretStore = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(secretDir);
        secretStore = new LocalSecretStore(secretDir);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Resolve_DocumentedPrecedence_RespectsHierarchy()
    {
        var resolver = new VariableResolver();
        var context = new VariableContext
        {
            ProjectDefaults = { ["host"] = "project.test", ["token"] = "proj-tok", ["tier"] = "free" },
            Collection = { ["host"] = "collection.test", ["token"] = "coll-tok" },
            Environment = { ["host"] = "env.test", ["token"] = "env-tok" },
            LocalEnvironment = { ["host"] = "local.test" },
            RunOverrides = { ["runVar"] = "override-value" },
            IterationData = { ["user"] = "alice" },
            RequestLocal = { ["user"] = "bob" }
        };

        // 1. LocalEnvironment overrides Environment, Collection, ProjectDefault
        var host = await resolver.ResolveAsync("https://{{host}}/api", context, "URL");
        Assert.Equal("https://local.test/api", host);

        // 2. Environment overrides Collection, ProjectDefault
        var token = await resolver.ResolveAsync("Bearer {{token}}", context, "Header");
        Assert.Equal("Bearer env-tok", token);

        // 3. ProjectDefault used when higher scopes don't define it
        var tier = await resolver.ResolveAsync("tier={{tier}}", context, "Query");
        Assert.Equal("tier=free", tier);

        // 4. RequestLocal overrides IterationData
        var user = await resolver.ResolveAsync("user={{user}}", context, "Body");
        Assert.Equal("user=bob", user);

        // 5. RunOverrides
        var runVar = await resolver.ResolveAsync("val={{runVar}}", context, "Header");
        Assert.Equal("val=override-value", runVar);
    }

    [Fact]
    public async Task Resolve_MissingVariable_FailsWithLocationAndName()
    {
        var resolver = new VariableResolver();
        var context = new VariableContext();

        var ex = await Assert.ThrowsAsync<VariableResolutionException>(() =>
            resolver.ResolveAsync("https://example.test/users/{{userId}}", context, "URL:RequestUsers"));

        Assert.Equal("userId", ex.VariableName);
        Assert.Equal("URL:RequestUsers", ex.Location);
        Assert.Contains("not defined", ex.Reason);
    }

    [Fact]
    public async Task Resolve_CircularReference_FailsWithCycleTrace()
    {
        var resolver = new VariableResolver();
        var context = new VariableContext
        {
            Environment =
            {
                ["A"] = "call-{{B}}",
                ["B"] = "call-{{C}}",
                ["C"] = "call-{{A}}"
            }
        };

        var ex = await Assert.ThrowsAsync<VariableResolutionException>(() =>
            resolver.ResolveAsync("{{A}}", context, "Header:X-Chain"));

        Assert.Equal("A", ex.VariableName);
        Assert.Equal("Header:X-Chain", ex.Location);
        Assert.Contains("Circular reference detected", ex.Reason);
        Assert.Contains("A -> B -> C -> A", ex.Reason);
    }

    [Fact]
    public async Task Resolve_NestingDepthExceeded_FailsGracefully()
    {
        var resolver = new VariableResolver();
        var context = new VariableContext();

        // Create a chain of 12 nested variables
        for (int i = 0; i < 15; i++)
        {
            context.Environment[$"level_{i}"] = $"{{{{level_{i + 1}}}}}";
        }
        context.Environment["level_15"] = "deepest";

        var ex = await Assert.ThrowsAsync<VariableResolutionException>(() =>
            resolver.ResolveAsync("{{level_0}}", context, "Body"));

        Assert.Contains("depth", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolve_DatasetCannotShadowSecret_AndInspectorRedactsValues()
    {
        var resolver = new VariableResolver();
        await secretStore.SetSecretAsync("apiToken", "super-secret-key-999");

        var context = new VariableContext
        {
            SecretStore = secretStore,
            IterationData = { ["apiToken"] = "plain-dataset-val" }
        };

        // {{apiToken}} resolves to the dataset column
        var plain = await resolver.ResolveAsync("{{apiToken}}", context, "Body");
        Assert.Equal("plain-dataset-val", plain);

        // {{secret:apiToken}} MUST resolve to the secret and NOT be shadowed by dataset!
        var secret = await resolver.ResolveAsync("{{secret:apiToken}}", context, "Header");
        Assert.Equal("super-secret-key-999", secret);

        // Inspect variables: secret value is redacted!
        var inspection = await resolver.InspectAsync("Data: {{apiToken}}, Auth: {{secret:apiToken}}", context);
        Assert.Equal(2, inspection.Count);

        var dataInspect = inspection.Single(i => i.Name == "apiToken");
        Assert.Equal("plain-dataset-val", dataInspect.RedactedValue);
        Assert.Equal(VariableSource.Dataset, dataInspect.Source);
        Assert.False(dataInspect.IsSecret);

        var secInspect = inspection.Single(i => i.Name == "secret:apiToken");
        Assert.Equal("[REDACTED]", secInspect.RedactedValue);
        Assert.Equal(VariableSource.Secret, secInspect.Source);
        Assert.True(secInspect.IsSecret);
    }

    [Fact]
    public async Task Resolve_DynamicHelpers_AreDeterministicWithFixedSeedAndTimestamp()
    {
        var resolver = new VariableResolver();
        var fixedTime = new DateTimeOffset(2026, 11, 7, 21, 30, 0, TimeSpan.Zero);
        var context1 = new VariableContext
        {
            DynamicContext = new DynamicValueContext
            {
                Seed = 12345,
                Timestamp = fixedTime,
                RunId = "run-abc-777",
                IterationIndex = 3
            }
        };

        var context2 = new VariableContext
        {
            DynamicContext = new DynamicValueContext
            {
                Seed = 12345,
                Timestamp = fixedTime,
                RunId = "run-abc-777",
                IterationIndex = 3
            }
        };

        const string template = "UUID={{$uuid}} TIME={{$timestamp}} ISO={{$isoTimestamp}} RAND={{$randomInt}} STR={{$randomString}} RUN={{$runId}} ITER={{$iteration}}";

        var run1 = await resolver.ResolveAsync(template, context1, "URL");
        var run2 = await resolver.ResolveAsync(template, context2, "URL");

        // Identical context yields bit-for-bit identical outcome
        Assert.Equal(run1, run2);
        Assert.Contains("TIME=1794087000", run1);
        Assert.Contains("ISO=2026-11-07T21:30:00.0000000+00:00", run1);
        Assert.Contains("RUN=run-abc-777", run1);
        Assert.Contains("ITER=3", run1);
    }

    [Fact]
    public void RedactionEngine_RedactsSecretsAcrossLogsAndHeaders()
    {
        var engine = new RedactionEngine();
        engine.RegisterSecret("super-secret-password-123");
        engine.RegisterSecret("secret-api-token");

        var text = "Failed request with token secret-api-token and super-secret-password-123 in payload.";
        var redacted = engine.Redact(text);

        Assert.DoesNotContain("secret-api-token", redacted);
        Assert.DoesNotContain("super-secret-password-123", redacted);
        Assert.Equal("Failed request with token [REDACTED] and [REDACTED] in payload.", redacted);

        var headers = new List<HeaderEntry>
        {
            new("Authorization", "Bearer sensitive-token-here"),
            new("X-Custom", "safe-value"),
            new("X-Api-Key", "my-key-abc")
        };

        var redactedHeaders = engine.RedactHeaders(headers);
        Assert.Equal("[REDACTED]", redactedHeaders.Single(h => h.Name == "Authorization").Value);
        Assert.Equal("[REDACTED]", redactedHeaders.Single(h => h.Name == "X-Api-Key").Value);
        Assert.Equal("safe-value", redactedHeaders.Single(h => h.Name == "X-Custom").Value);
    }

    [Fact]
    public async Task LocalSecretStore_SavesAndRetrievesSecrets()
    {
        await secretStore.SetSecretAsync("prod-db-pass", "P@ssw0rd!2026");
        await secretStore.SetSecretAsync("stripe-key", "sk_live_12345");

        var names = await secretStore.ListSecretNamesAsync();
        Assert.Contains("prod-db-pass", names);
        Assert.Contains("stripe-key", names);

        var pass = await secretStore.GetSecretAsync("prod-db-pass");
        Assert.Equal("P@ssw0rd!2026", pass);

        var missing = await secretStore.GetSecretAsync("non-existent");
        Assert.Null(missing);
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(secretDir)) Directory.Delete(secretDir, true);
        return Task.CompletedTask;
    }
}

