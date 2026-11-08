using System.Text.Json;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class PlaywrightTrafficTests
{
    [Fact]
    public void Playwright_PassingUi_WithFailingApiAssertion_FailsCombined_AndNamesScenario()
    {
        var service = new PlaywrightTrafficService();
        const string scenario = "Browser Checkout Flow";

        var knownRequests = new List<HttpRequestDefinition>
        {
            new HttpRequestDefinition
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Get Pricing",
                Method = "GET",
                Url = "https://store.example.com/api/pricing/cart123"
            }
        };

        var capturedTraffic = new List<CapturedTrafficRecord>
        {
            new CapturedTrafficRecord(
                Id: "cap-1",
                ScenarioName: scenario,
                Timestamp: DateTimeOffset.UtcNow,
                Method: "GET",
                Url: "https://store.example.com/api/pricing/cart123",
                Origin: "https://store.example.com",
                Path: "/api/pricing/cart123",
                ResponseStatusCode: 200,
                ResponseBody: "{\"pricing\": {\"total\": 120.0, \"discount\": 85.0}}"
            )
        };

        // Assert discount must be less than 50
        var assertions = new List<TrafficAssertionRule>
        {
            new TrafficAssertionRule(
                RuleId: "assert-max-discount",
                TargetProperty: "Body.pricing.discount",
                Operator: "LessThan",
                ExpectedValue: "50"
            )
        };

        // UI passed, but API pricing discount violates business rules
        var result = service.EvaluateScenario(
            scenarioName: scenario,
            uiPassed: true,
            traffic: capturedTraffic,
            knownRequests: knownRequests,
            customAssertions: assertions
        );

        Assert.True(result.UiPassed);
        Assert.False(result.ApiPassed);
        Assert.False(result.CombinedPassed);
        Assert.Equal("API", result.FailureSource);
        Assert.Equal(scenario, result.ScenarioName);
        Assert.NotNull(result.PrimaryFailureMessage);
        Assert.Contains(scenario, result.PrimaryFailureMessage);
        Assert.Contains("Expected Body.pricing.discount LessThan 50, but received 85", result.PrimaryFailureMessage);
    }

    [Fact]
    public void Playwright_AmbiguousAndUnmatchedTraffic_IsSurfaced_AndRedactsSecrets()
    {
        var service = new PlaywrightTrafficService();

        // Two identically scoring requests creates ambiguity
        var knownRequests = new List<HttpRequestDefinition>
        {
            new HttpRequestDefinition
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Name = "Users Query A",
                Method = "GET",
                Url = "https://api.example.com/api/users/{id}"
            },
            new HttpRequestDefinition
            {
                Id = Guid.Parse("33333333-3333-3333-3333-333333333333"),
                Name = "Users Query B",
                Method = "GET",
                Url = "https://api.example.com/api/users/{id}"
            }
        };

        var traffic = new List<CapturedTrafficRecord>
        {
            // 1. Ambiguous match
            new CapturedTrafficRecord(
                Id: "cap-ambiguous",
                ScenarioName: "User Profile Flow",
                Timestamp: DateTimeOffset.UtcNow,
                Method: "GET",
                Url: "https://api.example.com/api/users/u-42",
                Origin: "https://api.example.com",
                Path: "/api/users/u-42",
                Headers: new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer topsecret-jwt-token-12345",
                    ["Cookie"] = "session_id=abcdef123456",
                    ["X-Api-Key"] = "api_key_secret_777",
                    ["Accept"] = "application/json"
                },
                Body: "{\"password\": \"p@ssw0rd!\", \"token\": \"tok_999\", \"profile\": {\"name\": \"Alice\"}}",
                ResponseStatusCode: 200,
                ResponseHeaders: new Dictionary<string, string>
                {
                    ["Set-Cookie"] = "tracking_cookie=12345; Secure; HttpOnly",
                    ["Content-Type"] = "application/json"
                },
                ResponseBody: "{\"apiKey\": \"live_secret_444\", \"userId\": \"u-42\"}"
            ),
            // 2. Unmatched request
            new CapturedTrafficRecord(
                Id: "cap-unmatched",
                ScenarioName: "User Profile Flow",
                Timestamp: DateTimeOffset.UtcNow,
                Method: "POST",
                Url: "https://analytics.example.com/v1/telemetry",
                Origin: "https://analytics.example.com",
                Path: "/v1/telemetry",
                ResponseStatusCode: 204
            )
        };

        var result = service.EvaluateScenario(
            scenarioName: "User Profile Flow",
            uiPassed: true,
            traffic: traffic,
            knownRequests: knownRequests
        );

        // Ambiguous request is surfaced
        Assert.Single(result.AmbiguousTraffic);
        var amb = result.AmbiguousTraffic[0];
        Assert.Equal("cap-ambiguous", amb.Id);

        // Unmatched request is surfaced
        Assert.Single(result.UnmatchedTraffic);
        var un = result.UnmatchedTraffic[0];
        Assert.Equal("cap-unmatched", un.Id);

        // Verify secret redaction
        Assert.Equal("[REDACTED]", amb.Headers?["Authorization"]);
        Assert.Equal("[REDACTED]", amb.Headers?["Cookie"]);
        Assert.Equal("[REDACTED]", amb.Headers?["X-Api-Key"]);
        Assert.Equal("application/json", amb.Headers?["Accept"]);

        Assert.Equal("[REDACTED]", amb.ResponseHeaders?["Set-Cookie"]);

        // JSON bodies redacted
        Assert.Contains("[REDACTED]", amb.Body);
        Assert.DoesNotContain("p@ssw0rd!", amb.Body);
        Assert.DoesNotContain("tok_999", amb.Body);
        Assert.Contains("Alice", amb.Body);

        Assert.Contains("[REDACTED]", amb.ResponseBody);
        Assert.DoesNotContain("live_secret_444", amb.ResponseBody);
        Assert.Contains("u-42", amb.ResponseBody);
    }

    [Fact]
    public void Playwright_Limitations_AreExplicit_AndCiArtifactsGenerated()
    {
        var service = new PlaywrightTrafficService();
        string tempDir = Path.Combine(Path.GetTempPath(), "awr-pw-ci-" + Guid.NewGuid().ToString("N"));

        try
        {
            var traffic = new List<CapturedTrafficRecord>
            {
                new CapturedTrafficRecord(
                    Id: "cap-cached",
                    ScenarioName: "Catalog Flow",
                    Timestamp: DateTimeOffset.UtcNow,
                    Method: "GET",
                    Url: "https://store.example.com/assets/bundle.js",
                    Origin: "https://store.example.com",
                    Path: "/assets/bundle.js",
                    ResponseStatusCode: 304,
                    IsCached: true,
                    IsServiceWorker: true
                ),
                new CapturedTrafficRecord(
                    Id: "cap-ws",
                    ScenarioName: "Catalog Flow",
                    Timestamp: DateTimeOffset.UtcNow,
                    Method: "GET",
                    Url: "wss://store.example.com/realtime",
                    Origin: "https://store.example.com",
                    Path: "/realtime",
                    IsWebSocket: true
                ),
                new CapturedTrafficRecord(
                    Id: "cap-truncated",
                    ScenarioName: "Catalog Flow",
                    Timestamp: DateTimeOffset.UtcNow,
                    Method: "POST",
                    Url: "https://store.example.com/api/bigdata",
                    Origin: "https://store.example.com",
                    Path: "/api/bigdata",
                    Body: new string('x', 300_000) // exceeds default 256 KiB bound
                )
            };

            var scenarioResult = service.EvaluateScenario(
                scenarioName: "Catalog Flow",
                uiPassed: true,
                traffic: traffic,
                knownRequests: Array.Empty<HttpRequestDefinition>()
            );

            // Limitations must be explicitly disclosed
            Assert.Contains(scenarioResult.LimitationsEncountered, l => l.Contains("BrowserHttpCache"));
            Assert.Contains(scenarioResult.LimitationsEncountered, l => l.Contains("ServiceWorkerInterception"));
            Assert.Contains(scenarioResult.LimitationsEncountered, l => l.Contains("WebSocketTraffic"));
            Assert.Contains(scenarioResult.LimitationsEncountered, l => l.Contains("BoundedPayloadLimit"));

            // CI Artifact generation
            var summary = new PlaywrightSummaryReport(
                TotalScenarios: 1,
                PassedScenarios: 1,
                FailedScenarios: 0,
                FailingScenarios: Array.Empty<string>(),
                TotalCapturedRequests: traffic.Count,
                MatchedRequests: 0,
                UnmatchedRequests: traffic.Count,
                AmbiguousRequests: 0,
                TotalAssertionFailures: 0,
                Scenarios: new[] { scenarioResult }
            );

            service.GenerateCiArtifacts(summary, tempDir);

            string jsonReport = Path.Combine(tempDir, "playwright-api-report.json");
            string mdSummary = Path.Combine(tempDir, "playwright-api-summary.md");

            Assert.True(File.Exists(jsonReport));
            Assert.True(File.Exists(mdSummary));

            string jsonContent = File.ReadAllText(jsonReport);
            Assert.Contains("Catalog Flow", jsonContent);
            Assert.Contains("BrowserHttpCache", jsonContent);

            string mdContent = File.ReadAllText(mdSummary);
            Assert.Contains("# Playwright & API Combined Test Summary", mdContent);
            Assert.Contains("Catalog Flow", mdContent);
            Assert.Contains("PASSED", mdContent);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void Playwright_DependencyMap_AndDraftRequestGeneration()
    {
        var service = new PlaywrightTrafficService();

        var traffic = new List<CapturedTrafficRecord>
        {
            new CapturedTrafficRecord(
                Id: "cap-auth",
                ScenarioName: "Login Checkout Flow",
                Timestamp: DateTimeOffset.UtcNow,
                Method: "POST",
                Url: "https://example.com/api/v1/auth/login",
                Origin: "https://example.com",
                Path: "/api/v1/auth/login"
            ),
            new CapturedTrafficRecord(
                Id: "cap-cart",
                ScenarioName: "Login Checkout Flow",
                Timestamp: DateTimeOffset.UtcNow,
                Method: "GET",
                Url: "https://example.com/api/v1/cart",
                Origin: "https://example.com",
                Path: "/api/v1/cart"
            ),
            new CapturedTrafficRecord(
                Id: "cap-order",
                ScenarioName: "Login Checkout Flow",
                Timestamp: DateTimeOffset.UtcNow,
                Method: "POST",
                Url: "https://example.com/api/v1/orders",
                Origin: "https://example.com",
                Path: "/api/v1/orders",
                Body: "{\"orderTotal\": 50.0}"
            )
        };

        // 1. Dependency Map
        var depMap = service.BuildDependencyMap("Login Checkout Flow", traffic);
        Assert.Equal("Login Checkout Flow", depMap.ScenarioName);
        Assert.Equal(3, depMap.Endpoints.Count);
        Assert.Equal("POST /api/v1/auth/login", depMap.Endpoints[0]);
        Assert.Equal("GET /api/v1/cart", depMap.Endpoints[1]);
        Assert.Equal("POST /api/v1/orders", depMap.Endpoints[2]);

        Assert.Equal(3, depMap.Edges.Count);
        Assert.Equal("Login Checkout Flow", depMap.Edges[0].Caller);
        Assert.Equal("POST /api/v1/auth/login", depMap.Edges[0].Callee);

        Assert.Equal("POST /api/v1/auth/login", depMap.Edges[1].Caller);
        Assert.Equal("GET /api/v1/cart", depMap.Edges[1].Callee);

        Assert.Equal("GET /api/v1/cart", depMap.Edges[2].Caller);
        Assert.Equal("POST /api/v1/orders", depMap.Edges[2].Callee);

        // 2. Draft request generation from unmatched traffic
        var drafts = service.GenerateDraftRequests(traffic);
        Assert.Equal(3, drafts.Count);
        Assert.Equal("POST", drafts[0].Method);
        Assert.Equal("https://example.com/api/v1/auth/login", drafts[0].Url);
        Assert.Equal("Draft: POST /api/v1/auth/login", drafts[0].Name);

        Assert.Equal("POST", drafts[2].Method);
        Assert.Equal("https://example.com/api/v1/orders", drafts[2].Url);
        Assert.Equal("{\"orderTotal\": 50.0}", drafts[2].Body);
    }
}
