using ApiWrangler.Application;
using ApiWrangler.Execution;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class NodeScriptHostTests
{
    private static NodeScriptHost CreateHost(string? trustDirectory = null)
    {
        var runtime = new NodeRuntimeService();
        var trust = new ProjectTrustService(trustDirectory);
        string workerPath = Path.Combine(Directory.GetCurrentDirectory(), "workers", "node", "dist", "worker.js");
        if (!File.Exists(workerPath))
        {
            workerPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "workers", "node", "dist", "worker.js"));
        }
        return new NodeScriptHost(runtime, trust, workerPath);
    }

    [NodeFact]
    public async Task ExecuteAsync_NativeWranglerTestAndExpect_PassesAssertions()
    {
        var host = CreateHost();
        var request = new ScriptExecutionRequest(
            Code: """
            wrangler.test("Status code is 200", () => {
                wrangler.expect(response.status).toEqual(200);
            });
            wrangler.test("Response body parsed correctly", () => {
                const data = response.json();
                wrangler.expect(data.name).toBe("API Wrangler");
                wrangler.expect(data.items.length).toBeGreaterThan(0);
                wrangler.expect(data.tags).toContain("api");
            });
            variables.set("receivedName", response.json().name);
            console.log("Completed assertions for", response.status);
            """,
            Stage: "test",
            Response: new ScriptResponseContext(
                Status: 200,
                StatusText: "OK",
                Headers: new Dictionary<string, string> { ["content-type"] = "application/json" },
                Body: """{"name": "API Wrangler", "items": [1, 2, 3], "tags": ["api", "testing"]}""",
                ResponseTimeMs: 45,
                SizeBytes: 65
            )
        );

        ScriptExecutionResult result = await host.ExecuteAsync(request);

        Assert.True(result.Success, result.Error ?? "Success was false without error");
        Assert.Equal(ScriptOutcome.Passed, result.Outcome);
        Assert.Equal(2, result.TestResults.Count);
        Assert.All(result.TestResults, t => Assert.True(t.Passed));

        // Check variable mutation
        Assert.Single(result.VariableMutations);
        Assert.Equal("variables", result.VariableMutations[0].Scope);
        Assert.Equal("receivedName", result.VariableMutations[0].Key);
        Assert.Equal("API Wrangler", result.VariableMutations[0].Value?.GetString());

        // Check console logs
        Assert.Single(result.ConsoleLogs);
        Assert.Equal("log", result.ConsoleLogs[0].Level);
        Assert.Contains("Completed assertions for 200", result.ConsoleLogs[0].Message);
    }

    [NodeFact]
    public async Task ExecuteAsync_FailedAssertion_ReportsFailedOutcome()
    {
        var host = CreateHost();
        var request = new ScriptExecutionRequest(
            Code: """
            wrangler.test("Expect status 201", () => {
                wrangler.expect(response.status).toEqual(201);
            });
            """,
            Stage: "test",
            Response: new ScriptResponseContext(
                Status: 404,
                StatusText: "Not Found",
                Headers: null,
                Body: "Not Found",
                ResponseTimeMs: 12,
                SizeBytes: 9
            )
        );

        ScriptExecutionResult result = await host.ExecuteAsync(request);

        Assert.False(result.Success);
        Assert.Equal(ScriptOutcome.Failed, result.Outcome);
        Assert.Single(result.TestResults);
        Assert.False(result.TestResults[0].Passed);
        Assert.Contains("equal 201", result.TestResults[0].Error);
    }

    [NodeFact]
    public async Task ExecuteAsync_SyntaxError_ReportsErrorOutcome()
    {
        var host = CreateHost();
        var request = new ScriptExecutionRequest(
            Code: "this is invalid javascript ;;; {{{",
            Stage: "pre-request"
        );

        ScriptExecutionResult result = await host.ExecuteAsync(request);

        Assert.False(result.Success);
        Assert.Equal(ScriptOutcome.Error, result.Outcome);
        Assert.NotNull(result.Error);
    }

    [NodeFact]
    public async Task ExecuteAsync_InfiniteLoop_TerminatesCleanlyViaTimeout()
    {
        var host = CreateHost();
        var request = new ScriptExecutionRequest(
            Code: "while (true) { /* infinite spin */ }",
            Stage: "pre-request",
            Timeout: TimeSpan.FromSeconds(1) // 1 second timeout for test speed
        );

        ScriptExecutionResult result = await host.ExecuteAsync(request);

        Assert.False(result.Success);
        Assert.Equal(ScriptOutcome.TimedOut, result.Outcome);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [NodeFact]
    public async Task ExecuteAsync_ConsoleOutputFlood_IsBounded()
    {
        var host = CreateHost();
        var request = new ScriptExecutionRequest(
            Code: """
            for (let i = 0; i < 2000; i++) {
                console.log("flood line with lots of characters " + "x".repeat(100));
            }
            """,
            Stage: "pre-request"
        );

        ScriptExecutionResult result = await host.ExecuteAsync(request);

        Assert.True(result.Success);
        Assert.NotEmpty(result.ConsoleLogs);

        int totalBytes = result.ConsoleLogs.Sum(l => l.Message.Length);
        Assert.InRange(totalBytes, 1, 128 * 1024); // Verify it was capped and did not overflow
    }

    [Fact]
    public async Task ExecuteAsync_UntrustedProject_ThrowsUntrustedException()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "awr-host-" + Guid.NewGuid().ToString("N"));
        string trustDir = Path.Combine(tempDir, "trust");
        string projectDir = Path.Combine(tempDir, "proj");
        Directory.CreateDirectory(projectDir);

        try
        {
            var host = CreateHost(trustDir);
            var request = new ScriptExecutionRequest(
                Code: "variables.set('ran', true);",
                Stage: "pre-request",
                ProjectPath: projectDir
            );

            await Assert.ThrowsAsync<UntrustedScriptExecutionException>(() => host.ExecuteAsync(request));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
