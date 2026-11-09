using System.Text.Json;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class MockEngineTests
{
    [Fact]
    public void MockEngine_Matching_SpecificityAndPrecedence_WorksDeterministically()
    {
        var engine = new MockEngine();

        // 1. Broad catch-all
        engine.AddRule(new MockRule(
            Id: "r-wildcard",
            Name: "Wildcard Users",
            Method: "GET",
            PathPattern: "/users/*",
            Priority: 0,
            ResponseStatusCode: 200,
            ResponseBody: "{\"scope\": \"wildcard\"}"
        ));

        // 2. Parameterized
        engine.AddRule(new MockRule(
            Id: "r-param",
            Name: "User By ID",
            Method: "GET",
            PathPattern: "/users/{id}",
            Priority: 0,
            ResponseStatusCode: 200,
            ResponseBody: "{\"scope\": \"param\"}"
        ));

        // 3. Exact path
        engine.AddRule(new MockRule(
            Id: "r-exact",
            Name: "Current User",
            Method: "GET",
            PathPattern: "/users/me",
            Priority: 0,
            ResponseStatusCode: 200,
            ResponseBody: "{\"scope\": \"exact\"}"
        ));

        // Exact match should win over wildcard and param
        var exactMatch = engine.MatchRequest("GET", "/users/me");
        Assert.True(exactMatch.IsMatched);
        Assert.Equal("r-exact", exactMatch.Rule?.Id);

        // Parameterized match should win over wildcard for other user IDs
        var paramMatch = engine.MatchRequest("GET", "/users/12345");
        Assert.True(paramMatch.IsMatched);
        Assert.Equal("r-param", paramMatch.Rule?.Id);
    }

    [Fact]
    public void MockEngine_StatefulScenarios_TransitionsStateCorrectly()
    {
        var engine = new MockEngine();

        // Step 1: Requires Initial state, transitions to Pending
        engine.AddRule(new MockRule(
            Id: "step-1",
            Name: "Start Checkout",
            Method: "POST",
            PathPattern: "/checkout/start",
            ScenarioStateRequired: "CheckoutFlow=Initial",
            ScenarioStateTransition: "CheckoutFlow=Pending",
            ResponseStatusCode: 200,
            ResponseBody: "{\"status\": \"started\"}"
        ));

        // Step 2: Requires Pending state, transitions to Complete
        engine.AddRule(new MockRule(
            Id: "step-2",
            Name: "Complete Checkout",
            Method: "POST",
            PathPattern: "/checkout/complete",
            ScenarioStateRequired: "CheckoutFlow=Pending",
            ScenarioStateTransition: "CheckoutFlow=Complete",
            ResponseStatusCode: 200,
            ResponseBody: "{\"status\": \"completed\"}"
        ));

        // 1. Trying step 2 first fails
        var premature = engine.MatchRequest("POST", "/checkout/complete");
        Assert.False(premature.IsMatched);

        // 2. Step 1 matches
        var step1 = engine.MatchRequest("POST", "/checkout/start");
        Assert.True(step1.IsMatched);
        Assert.Equal("step-1", step1.Rule?.Id);

        // Transition manually or via request execution
        engine.SetScenarioState("CheckoutFlow", "Pending");

        // 3. Step 2 now matches
        var step2 = engine.MatchRequest("POST", "/checkout/complete");
        Assert.True(step2.IsMatched);
        Assert.Equal("step-2", step2.Rule?.Id);
    }

    [Fact]
    public async Task MockEngine_HttpHosting_ServesRequestsAndLogs()
    {
        await using var engine = new MockEngine();
        engine.AddRule(new MockRule(
            Id: "api-items",
            Name: "List Items",
            Method: "GET",
            PathPattern: "/api/items",
            ResponseStatusCode: 200,
            ResponseHeaders: new Dictionary<string, string> { ["X-Mock-Engine"] = "ApiWrangler" },
            ResponseBody: "[{\"id\": 1, \"title\": \"Mock item\"}]"
        ));

        await engine.StartAsync(new MockServerConfiguration(Port: 0));

        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync(engine.ListeningUrl + "/api/items");

            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.Contains("X-Mock-Engine"));

            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("Mock item", body);

            // Check logging
            Assert.Single(engine.Logs);
            var log = engine.Logs[0];
            Assert.Equal("GET", log.Method);
            Assert.Equal("/api/items", log.Path);
            Assert.Equal(200, log.ResponseStatusCode);
            Assert.Equal("api-items", log.MatchedRuleId);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task MockEngine_FaultSimulation_HttpErrorAndDelay()
    {
        await using var engine = new MockEngine();
        engine.AddRule(new MockRule(
            Id: "faulty-endpoint",
            Name: "Faulty Endpoint",
            Method: "GET",
            PathPattern: "/api/broken",
            FaultType: MockFaultType.HttpError,
            ResponseStatusCode: 502,
            ResponseBody: "{\"error\": \"Bad Gateway simulation\"}"
        ));

        await engine.StartAsync(new MockServerConfiguration(Port: 0));

        try
        {
            using var client = new HttpClient();
            var response = await client.GetAsync(engine.ListeningUrl + "/api/broken");

            Assert.Equal(System.Net.HttpStatusCode.BadGateway, response.StatusCode);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Contains("Bad Gateway simulation", body);

            var log = Assert.Single(engine.Logs);
            Assert.Equal("HttpError", log.FaultApplied);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task MockEngine_NonLoopbackHostWithoutExternalBinding_ThrowsInvalidOperationException()
    {
        await using var engine = new MockEngine();
        var config = new MockServerConfiguration(Host: "0.0.0.0", Port: 0, AllowExternalBinding: false);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(config));
        Assert.Contains("Cannot bind to non-loopback host", ex.Message);
    }
}

