using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class HostedMockTests
{
    [Fact]
    public async Task HostedMock_ExternalBinding_WithoutAuthToken_ThrowsInvalidOperationException()
    {
        await using var engine = new MockEngine();
        var config = new MockServerConfiguration(
            Host: "0.0.0.0",
            Port: 0,
            AllowExternalBinding: true,
            AuthToken: null
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(config));
        Assert.Contains("Remote exposure requires explicit authentication configuration", ex.Message);
    }

    [Fact]
    public async Task HostedMock_RequireAuthentication_WithoutAuthToken_ThrowsInvalidOperationException()
    {
        await using var engine = new MockEngine();
        var config = new MockServerConfiguration(
            Host: "127.0.0.1",
            Port: 0,
            AllowExternalBinding: false,
            RequireAuthentication: true,
            AuthToken: ""
        );

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(config));
        Assert.Contains("RequireAuthentication is enabled but no AuthToken was configured", ex.Message);
    }

    [Fact]
    public async Task HostedMock_AuthenticationEnforcement_RejectsUnauthorizedRequests()
    {
        await using var engine = new MockEngine();
        engine.AddRule(new MockRule(
            Id: "secured-resource",
            Name: "Protected Endpoint",
            Method: "GET",
            PathPattern: "/api/secured",
            ResponseStatusCode: 200,
            ResponseBody: "{\"secret\": \"data\"}"
        ));

        const string validToken = "shared-mock-secret-key-12345";
        var config = new MockServerConfiguration(
            Host: "127.0.0.1",
            Port: 0,
            AuthToken: validToken,
            RequireAuthentication: true
        );

        await engine.StartAsync(config);

        try
        {
            using var client = new HttpClient();

            // 1. Unauthenticated request -> 401
            var unauthResponse = await client.GetAsync(engine.ListeningUrl + "/api/secured");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthResponse.StatusCode);
            string unauthBody = await unauthResponse.Content.ReadAsStringAsync();
            Assert.Contains("Unauthorized", unauthBody);

            // 2. Invalid Bearer token -> 401
            var invalidRequest = new HttpRequestMessage(HttpMethod.Get, engine.ListeningUrl + "/api/secured");
            invalidRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");
            var invalidResponse = await client.SendAsync(invalidRequest);
            Assert.Equal(HttpStatusCode.Unauthorized, invalidResponse.StatusCode);

            // 3. Valid Bearer token -> 200
            var validBearerRequest = new HttpRequestMessage(HttpMethod.Get, engine.ListeningUrl + "/api/secured");
            validBearerRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", validToken);
            var validBearerResponse = await client.SendAsync(validBearerRequest);
            Assert.Equal(HttpStatusCode.OK, validBearerResponse.StatusCode);
            string bearerBody = await validBearerResponse.Content.ReadAsStringAsync();
            Assert.Contains("data", bearerBody);

            // 4. Valid X-Api-Key -> 200
            var validApiKeyRequest = new HttpRequestMessage(HttpMethod.Get, engine.ListeningUrl + "/api/secured");
            validApiKeyRequest.Headers.Add("X-Api-Key", validToken);
            var validApiKeyResponse = await client.SendAsync(validApiKeyRequest);
            Assert.Equal(HttpStatusCode.OK, validApiKeyResponse.StatusCode);
            string apiKeyBody = await validApiKeyResponse.Content.ReadAsStringAsync();
            Assert.Contains("data", apiKeyBody);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task HostedMock_SemanticEquivalence_WithLocalEngine()
    {
        // Define exact same rules for local and hosted
        var rules = new List<MockRule>
        {
            new MockRule(
                Id: "rule-init",
                Name: "Step 1 Initialize",
                Method: "POST",
                PathPattern: "/order/init",
                ScenarioStateRequired: "OrderState=Initial",
                ScenarioStateTransition: "OrderState=Processing",
                ResponseStatusCode: 201,
                ResponseBody: "{\"orderState\": \"Processing\"}"
            ),
            new MockRule(
                Id: "rule-complete",
                Name: "Step 2 Complete",
                Method: "POST",
                PathPattern: "/order/complete",
                ScenarioStateRequired: "OrderState=Processing",
                ScenarioStateTransition: "OrderState=Completed",
                ResponseStatusCode: 200,
                ResponseBody: "{\"orderState\": \"Completed\"}"
            )
        };

        // 1. Run against Local engine (unauthenticated, loopback)
        await using var localEngine = new MockEngine();
        foreach (var r in rules) localEngine.AddRule(r);
        await localEngine.StartAsync(new MockServerConfiguration(Port: 0));

        // 2. Run against Hosted engine (authenticated, external binding permitted)
        const string hostedToken = "hosted-auth-key-abc";
        await using var hostedEngine = new MockEngine();
        foreach (var r in rules) hostedEngine.AddRule(r);
        await hostedEngine.StartAsync(new MockServerConfiguration(
            Host: "127.0.0.1",
            Port: 0,
            AllowExternalBinding: false,
            AuthToken: hostedToken,
            RequireAuthentication: true
        ));

        try
        {
            using var localClient = new HttpClient();
            using var hostedClient = new HttpClient();
            hostedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", hostedToken);

            // Step A: Premature complete should fail on both
            var localPremature = await localClient.PostAsync(localEngine.ListeningUrl + "/order/complete", null);
            var hostedPremature = await hostedClient.PostAsync(hostedEngine.ListeningUrl + "/order/complete", null);
            Assert.Equal(HttpStatusCode.NotFound, localPremature.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, hostedPremature.StatusCode);

            // Step B: Step 1 Init succeeds on both and transitions to Processing
            var localInit = await localClient.PostAsync(localEngine.ListeningUrl + "/order/init", null);
            var hostedInit = await hostedClient.PostAsync(hostedEngine.ListeningUrl + "/order/init", null);
            Assert.Equal(HttpStatusCode.Created, localInit.StatusCode);
            Assert.Equal(HttpStatusCode.Created, hostedInit.StatusCode);

            // Step C: Step 2 Complete succeeds on both now that state has transitioned
            var localComplete = await localClient.PostAsync(localEngine.ListeningUrl + "/order/complete", null);
            var hostedComplete = await hostedClient.PostAsync(hostedEngine.ListeningUrl + "/order/complete", null);
            Assert.Equal(HttpStatusCode.OK, localComplete.StatusCode);
            Assert.Equal(HttpStatusCode.OK, hostedComplete.StatusCode);

            // Final state in scenario states dictionary matches
            Assert.Equal("Completed", localEngine.ScenarioStates["OrderState"]);
            Assert.Equal("Completed", hostedEngine.ScenarioStates["OrderState"]);
        }
        finally
        {
            await localEngine.StopAsync();
            await hostedEngine.StopAsync();
        }
    }

    [Fact]
    public async Task HostedMock_AdminEndpoints_Health_Logs_ResetState()
    {
        const string adminKey = "adm-sec-999";
        const string clientToken = "client-tok-111";

        await using var engine = new MockEngine();
        engine.AddRule(new MockRule(
            Id: "item-rule",
            Name: "Items",
            Method: "GET",
            PathPattern: "/items",
            ScenarioStateRequired: "ItemFlow=Initial",
            ScenarioStateTransition: "ItemFlow=Touched",
            ResponseStatusCode: 200,
            ResponseBody: "[]"
        ));

        var config = new MockServerConfiguration(
            Port: 0,
            AuthToken: clientToken,
            AdminApiKey: adminKey,
            RequireAuthentication: true
        );

        await engine.StartAsync(config);

        try
        {
            using var client = new HttpClient();

            // 1. Health endpoint is public for readiness / liveness probes
            var healthResp = await client.GetAsync(engine.ListeningUrl + "/__admin/health");
            Assert.Equal(HttpStatusCode.OK, healthResp.StatusCode);
            string healthJson = await healthResp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(healthJson);
            Assert.True(doc.RootElement.GetProperty("isListening").GetBoolean());
            Assert.Equal(1, doc.RootElement.GetProperty("activeRulesCount").GetInt32());

            // 2. Perform authenticated request
            var getReq = new HttpRequestMessage(HttpMethod.Get, engine.ListeningUrl + "/items");
            getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", clientToken);
            var getResp = await client.SendAsync(getReq);
            Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
            Assert.Equal("Touched", engine.ScenarioStates["ItemFlow"]);

            // 3. Admin logs retrieval
            var logsReq = new HttpRequestMessage(HttpMethod.Get, engine.ListeningUrl + "/__admin/logs");
            logsReq.Headers.Add("X-Api-Key", adminKey);
            var logsResp = await client.SendAsync(logsReq);
            Assert.Equal(HttpStatusCode.OK, logsResp.StatusCode);
            string logsJson = await logsResp.Content.ReadAsStringAsync();
            Assert.Contains("/items", logsJson);

            // 4. Admin state reset
            var resetReq = new HttpRequestMessage(HttpMethod.Post, engine.ListeningUrl + "/__admin/state/reset");
            resetReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
            var resetResp = await client.SendAsync(resetReq);
            Assert.Equal(HttpStatusCode.OK, resetResp.StatusCode);

            // Scenario state is now cleared back to default
            Assert.False(engine.ScenarioStates.ContainsKey("ItemFlow"));
            Assert.Empty(engine.Logs);
        }
        finally
        {
            await engine.StopAsync();
        }
    }

    [Fact]
    public async Task HostedMock_BoundedLogRetention_EnforcesCap()
    {
        await using var engine = new MockEngine();
        engine.AddRule(new MockRule(
            Id: "ping-rule",
            Name: "Ping",
            Method: "GET",
            PathPattern: "/ping",
            ResponseStatusCode: 200,
            ResponseBody: "pong"
        ));

        const int maxLogs = 5;
        var config = new MockServerConfiguration(
            Port: 0,
            MaxLogRetention: maxLogs
        );

        await engine.StartAsync(config);

        try
        {
            using var client = new HttpClient();
            for (int i = 0; i < 15; i++)
            {
                var resp = await client.GetAsync(engine.ListeningUrl + "/ping");
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            }

            // Engine log count should be strictly capped at 5
            Assert.Equal(maxLogs, engine.Logs.Count);
        }
        finally
        {
            await engine.StopAsync();
        }
    }
}

