using System.Diagnostics;
using System.Text.Json;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class ContractAndCatalogTests
{
    [Fact]
    public void ContractDiff_DetectsBreakingChanges_RemovedEndpoint_RemovedMethod_NewRequiredField()
    {
        var diffEngine = new ContractDiffEngine();

        string baselineOpenApi = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Store API v1" },
          "paths": {
            "/api/users": {
              "get": {
                "summary": "Get users",
                "responses": { "200": { "description": "OK" } }
              },
              "delete": {
                "summary": "Delete user",
                "responses": { "204": { "description": "No Content" } }
              }
            },
            "/api/legacy": {
              "get": {
                "summary": "Legacy endpoint",
                "responses": { "200": { "description": "OK" } }
              }
            }
          }
        }
        """;

        string targetOpenApi = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Store API v2" },
          "paths": {
            "/api/users": {
              "get": {
                "summary": "Get users",
                "parameters": [
                  { "name": "tenantId", "in": "query", "required": true, "schema": { "type": "string" } }
                ],
                "responses": { "200": { "description": "OK" } }
              }
            }
          }
        }
        """;

        var report = diffEngine.CompareOpenApiContracts(baselineOpenApi, targetOpenApi);

        Assert.Equal("Store API v1", report.BaselineTitle);
        Assert.Equal("Store API v2", report.TargetTitle);
        Assert.True(report.TotalBreakingChanges >= 3);

        // 1. Removed endpoint
        Assert.Contains(report.Changes, c => c.Category == "RemovedEndpoint" && c.Path == "/api/legacy");

        // 2. Removed method
        Assert.Contains(report.Changes, c => c.Category == "RemovedMethod" && c.Path == "/api/users" && c.Method == "DELETE");

        // 3. Newly required field
        Assert.Contains(report.Changes, c => c.Category == "NewRequiredField" && c.Path == "/api/users" && c.Description.Contains("tenantId"));
    }

    [Fact]
    public void DocumentationGenerator_OmitsSecrets_AndGeneratesStaticSite()
    {
        var generator = new DocumentationGenerator();
        string tempDir = Path.Combine(Path.GetTempPath(), "awr-docs-" + Guid.NewGuid().ToString("N"));

        try
        {
            var req = new HttpRequestDefinition
            {
                Id = Guid.NewGuid(),
                Name = "Get Secret User Profile",
                Description = "Fetches the authenticated user profile.",
                Method = "GET",
                Url = "https://api.example.com/v1/user/profile",
                Auth = new RequestAuthDefinition { Type = "bearer", Token = "actual-secret-bearer-token-12345" },
                Headers = new List<HeaderEntry>
                {
                    new("Authorization", "Bearer actual-secret-bearer-token-12345", true),
                    new("Cookie", "session=abcdef-secret-value", true),
                    new("X-Api-Key", "live-secret-api-key", true),
                    new("Accept", "application/json", true)
                },
                Body = "{\"userId\": \"u-123\"}"
            };

            generator.PublishDocumentation(tempDir, "Production Service", new[] { req });

            string mdFile = Path.Combine(tempDir, "api-reference.md");
            string htmlFile = Path.Combine(tempDir, "index.html");

            Assert.True(File.Exists(mdFile));
            Assert.True(File.Exists(htmlFile));

            string mdContent = File.ReadAllText(mdFile);
            string htmlContent = File.ReadAllText(htmlFile);

            // Plaintext secret tokens and passwords must NEVER appear in generated documentation
            Assert.DoesNotContain("actual-secret-bearer-token-12345", mdContent);
            Assert.DoesNotContain("abcdef-secret-value", mdContent);
            Assert.DoesNotContain("live-secret-api-key", mdContent);

            Assert.DoesNotContain("actual-secret-bearer-token-12345", htmlContent);
            Assert.DoesNotContain("abcdef-secret-value", htmlContent);
            Assert.DoesNotContain("live-secret-api-key", htmlContent);

            // Redacted masks or instructions appear instead
            Assert.Contains("[REDACTED_SECRET]", mdContent);
            Assert.Contains("[REDACTED_SECRET]", htmlContent);
            Assert.Contains("secret values are omitted", mdContent);
            Assert.Contains("secret values are omitted", htmlContent);
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
    public void ApiCatalog_TenThousandRequests_SearchPerformance()
    {
        var catalog = new ApiCatalogIndex();

        // Generate 10,000 synthetic requests
        var requests = new List<HttpRequestDefinition>(10_000);
        for (int i = 0; i < 10_000; i++)
        {
            string method = (i % 3) switch
            {
                0 => "GET",
                1 => "POST",
                _ => "PUT"
            };

            requests.Add(new HttpRequestDefinition
            {
                Id = Guid.NewGuid(),
                Name = $"Request Operation {i} for Service {(i % 50)}",
                Method = method,
                Url = $"https://api.example.com/v1/entities/{i}/subresource"
            });
        }

        var indexWatch = Stopwatch.StartNew();
        catalog.IndexRequests(requests, "Core Service Collection");
        indexWatch.Stop();

        Assert.Equal(10_000, catalog.TotalIndexed);

        // Perform measured search across 10,000 requests
        var searchWatch = Stopwatch.StartNew();
        var searchResults = catalog.Search("Operation 9500", limit: 10);
        searchWatch.Stop();

        // Performance requirement: query across 10,000 requests must execute well under 50ms (sub-millisecond or few milliseconds)
        Assert.True(searchWatch.ElapsedMilliseconds < 50, $"Search latency exceeded budget: {searchWatch.ElapsedMilliseconds} ms");

        Assert.NotEmpty(searchResults);
        Assert.Contains("9500", searchResults[0].Name);
    }

    [Fact]
    public void SdkGenerator_PreviewSeparation_CSharp_TypeScript_Python()
    {
        var generator = new SdkGenerator();
        string previewDir = Path.Combine(Path.GetTempPath(), "awr-sdk-preview-" + Guid.NewGuid().ToString("N"));

        try
        {
            var requests = new List<HttpRequestDefinition>
            {
                new HttpRequestDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = "Get Cart",
                    Method = "GET",
                    Url = "https://store.example.com/api/v1/cart"
                },
                new HttpRequestDefinition
                {
                    Id = Guid.NewGuid(),
                    Name = "Checkout Order",
                    Method = "POST",
                    Url = "https://store.example.com/api/v1/checkout",
                    Body = "{\"cartId\": \"c-123\"}"
                }
            };

            // 1. CSharp SDK Preview
            var csResult = generator.GenerateSdk(requests, SdkGenerationTarget.CSharp, Path.Combine(previewDir, "csharp"));
            Assert.Single(csResult.Files);
            Assert.Equal("ApiClient.cs", csResult.Files[0].RelativePath);
            Assert.Contains("Task<HttpResponseMessage> GET_Get_CartAsync", csResult.Files[0].Content);
            Assert.Contains("Task<HttpResponseMessage> POST_Checkout_OrderAsync", csResult.Files[0].Content);

            // 2. TypeScript SDK Preview
            var tsResult = generator.GenerateSdk(requests, SdkGenerationTarget.TypeScript, Path.Combine(previewDir, "ts"));
            Assert.Single(tsResult.Files);
            Assert.Equal("apiClient.ts", tsResult.Files[0].RelativePath);
            Assert.Contains("async gET_Get_Cart", tsResult.Files[0].Content);
            Assert.Contains("async pOST_Checkout_Order", tsResult.Files[0].Content);

            // 3. Python SDK Preview
            var pyResult = generator.GenerateSdk(requests, SdkGenerationTarget.Python, Path.Combine(previewDir, "py"));
            Assert.Single(pyResult.Files);
            Assert.Equal("api_client.py", pyResult.Files[0].RelativePath);
            Assert.Contains("def get_get_cart", pyResult.Files[0].Content);
            Assert.Contains("def post_checkout_order", pyResult.Files[0].Content);

            // Ensure preview files exist on disk in the isolated preview directory
            Assert.True(File.Exists(Path.Combine(previewDir, "csharp", "ApiClient.cs")));
            Assert.True(File.Exists(Path.Combine(previewDir, "ts", "apiClient.ts")));
            Assert.True(File.Exists(Path.Combine(previewDir, "py", "api_client.py")));
        }
        finally
        {
            if (Directory.Exists(previewDir))
            {
                Directory.Delete(previewDir, true);
            }
        }
    }
}

