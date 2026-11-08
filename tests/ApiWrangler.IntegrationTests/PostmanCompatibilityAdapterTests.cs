using System.Net;
using ApiWrangler.Application;
using ApiWrangler.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class PostmanCompatibilityAdapterTests
{
    private static NodeScriptHost CreateHost()
    {
        var runtime = new NodeRuntimeService();
        var trust = new ProjectTrustService();
        return new NodeScriptHost(runtime, trust);
    }

    [NodeFact]
    public async Task PostmanAdapter_ExecutesRealWorldFixtureScript()
    {
        var host = CreateHost();
        var request = new ScriptExecutionRequest(
            Code: """
            pm.test("Status code is 200", function () {
                pm.response.to.have.status(200);
                pm.response.to.be.ok;
            });

            pm.test("Headers are present", function () {
                pm.response.to.have.header("content-type");
                pm.expect(pm.response.headers.get("content-type")).to.include("application/json");
            });

            pm.test("Body schema and properties", function () {
                var jsonData = pm.response.json();
                pm.expect(jsonData.orderId).to.equal(12345);
                pm.expect(jsonData.customer.name).to.eql("Alice");
                pm.expect(jsonData.tags).to.include("urgent");
                pm.expect(jsonData.total).to.be.above(50);
                pm.expect(jsonData.total).to.be.at.least(99.95);
            });

            pm.environment.set("lastOrderId", pm.response.json().orderId);
            pm.collectionVariables.set("customerName", pm.response.json().customer.name);
            postman.setNextRequest("ConfirmOrder");
            """,
            Stage: "test",
            Response: new ScriptResponseContext(
                Status: 200,
                StatusText: "OK",
                Headers: new Dictionary<string, string> { ["content-type"] = "application/json; charset=utf-8" },
                Body: """{"orderId": 12345, "customer": {"name": "Alice"}, "tags": ["urgent", "express"], "total": 99.95}""",
                ResponseTimeMs: 38,
                SizeBytes: 95
            ),
            Environment: new Dictionary<string, object?> { ["baseUrl"] = "https://example.com" }
        );

        ScriptExecutionResult result = await host.ExecuteAsync(request);

        Assert.True(result.Success, result.Error);
        Assert.Equal(ScriptOutcome.Passed, result.Outcome);
        Assert.Equal(3, result.TestResults.Count);
        Assert.All(result.TestResults, t => Assert.True(t.Passed));

        // Check environment mutation
        Assert.Contains(result.VariableMutations, m => m.Scope == "environment" && m.Key == "lastOrderId" && m.Value?.GetInt32() == 12345);
        Assert.Contains(result.VariableMutations, m => m.Scope == "collectionVariables" && m.Key == "customerName" && m.Value?.GetString() == "Alice");

        // Check flow control
        Assert.Equal("ConfirmOrder", result.NextRequest);
    }

    [NodeFact]
    public async Task PostmanAdapter_LegacyTestsDictionary_AndGlobals_Pass()
    {
        var host = CreateHost();
        var request = new ScriptExecutionRequest(
            Code: """
            tests["Status code is 200"] = responseCode.code === 200;
            tests["Body contains success"] = responseBody.has ? responseBody.has("success") : responseBody.includes("success");

            postman.setEnvironmentVariable("authToken", "secret-token-123");
            """,
            Stage: "test",
            Response: new ScriptResponseContext(
                Status: 200,
                StatusText: "OK",
                Headers: null,
                Body: "Operation success: true",
                ResponseTimeMs: 15,
                SizeBytes: 24
            )
        );

        ScriptExecutionResult result = await host.ExecuteAsync(request);

        Assert.True(result.Success, result.Error);
        Assert.Equal(ScriptOutcome.Passed, result.Outcome);
        Assert.Equal(2, result.TestResults.Count);
        Assert.All(result.TestResults, t => Assert.True(t.Passed));

        Assert.Contains(result.VariableMutations, m => m.Scope == "environment" && m.Key == "authToken" && m.Value?.GetString() == "secret-token-123");
    }

    [NodeFact]
    public async Task PostmanAdapter_UnsupportedApis_FailVisibly()
    {
        var host = CreateHost();

        // 1. pm.visualizer
        var visualizerReq = new ScriptExecutionRequest(
            Code: "pm.visualizer.set('<h1>template</h1>');",
            Stage: "test"
        );
        ScriptExecutionResult visResult = await host.ExecuteAsync(visualizerReq);
        Assert.False(visResult.Success);
        Assert.Equal(ScriptOutcome.Error, visResult.Outcome);
        Assert.Contains("pm.visualizer is not supported in API Wrangler", visResult.Error);

        // 2. pm.cookies programmatic jar
        var cookieReq = new ScriptExecutionRequest(
            Code: "const jar = pm.cookies.jar();",
            Stage: "test"
        );
        ScriptExecutionResult cookieResult = await host.ExecuteAsync(cookieReq);
        Assert.False(cookieResult.Success);
        Assert.Equal(ScriptOutcome.Error, cookieResult.Outcome);
        Assert.Contains("pm.cookies programmatic jar is not supported", cookieResult.Error);
    }

    [NodeFact]
    public async Task PostmanAdapter_SendRequest_ExecutesSecondaryRequest()
    {
        // Spin up a local lightweight fixture server
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapGet("/secondary", () => Results.Json(new { secondaryEcho = "verified" }));
        await app.StartAsync();

        try
        {
            string url = app.Urls.First() + "/secondary";
            var host = CreateHost();
            var request = new ScriptExecutionRequest(
                Code: $$"""
                pm.test("Secondary request executes", async function () {
                    const res = await pm.sendRequest("{{url}}");
                    pm.expect(res.code).to.equal(200);
                    const data = res.json();
                    pm.expect(data.secondaryEcho).to.equal("verified");
                });
                """,
                Stage: "test"
            );

            ScriptExecutionResult result = await host.ExecuteAsync(request);

            Assert.True(result.Success, result.Error);
            Assert.Equal(ScriptOutcome.Passed, result.Outcome);
            Assert.Single(result.TestResults);
            Assert.True(result.TestResults[0].Passed);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void CompatibilityService_DetectsSupportedAndUnsupportedFeatures()
    {
        var service = new PostmanCompatibilityService();

        string supportedScript = """
        pm.test("Status test", () => {
            pm.response.to.have.status(200);
            pm.expect(pm.response.json().id).to.equal(1);
        });
        pm.environment.set("key", "val");
        postman.setNextRequest("Step2");
        """;

        PostmanCompatibilityReport report = service.AnalyzeScript(supportedScript);
        Assert.True(report.IsCompatible);
        Assert.Empty(report.UnsupportedFeatures);
        Assert.Contains("pm.test", report.SupportedFeatures);
        Assert.Contains("pm.expect", report.SupportedFeatures);
        Assert.Contains("pm.response", report.SupportedFeatures);
        Assert.Contains("pm.environment", report.SupportedFeatures);
        Assert.Contains("postman.setNextRequest", report.SupportedFeatures);

        string unsupportedScript = """
        pm.visualizer.set("<div>test</div>");
        const jar = pm.cookies.jar();
        pm.execution.skipRequest();
        """;

        PostmanCompatibilityReport unsuppReport = service.AnalyzeScript(unsupportedScript);
        Assert.False(unsuppReport.IsCompatible);
        Assert.Equal(3, unsuppReport.UnsupportedFeatures.Count);
        Assert.Contains(unsuppReport.UnsupportedFeatures, f => f.Contains("pm.visualizer"));
        Assert.Contains(unsuppReport.UnsupportedFeatures, f => f.Contains("pm.cookies"));
        Assert.Contains(unsuppReport.UnsupportedFeatures, f => f.Contains("pm.execution.skipRequest"));
    }
}
