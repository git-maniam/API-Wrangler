using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Storage;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class ProjectImportExportTests
{
    private static async Task<IProjectSession> CreateInitializedProjectSessionAsync(string root)
    {
        var repo = new ProjectRepository();
        return await repo.InitializeAsync(root, "Test Import Project");
    }

    [Fact]
    public void CurlParser_SimpleGet_ExtractsMethodAndUrl()
    {
        string curl = "curl https://api.example.com/v1/users?page=2 -H 'Accept: application/json'";
        HttpRequestDefinition req = CurlParser.Parse(curl);

        Assert.Equal("GET", req.Method);
        Assert.Equal("https://api.example.com/v1/users?page=2", req.Url);
        Assert.Single(req.Headers);
        Assert.Equal("Accept", req.Headers[0].Name);
        Assert.Equal("application/json", req.Headers[0].Value);
        Assert.Single(req.Query);
        Assert.Equal("page", req.Query[0].Name);
        Assert.Equal("2", req.Query[0].Value);
    }

    [Fact]
    public void CurlParser_ComplexPostWithAuthAndFlags_ParsesCorrectly()
    {
        string curl = """
            curl -X POST https://api.example.com/login \
                 -H "Content-Type: application/json" \
                 -u "admin:secret123" \
                 -d '{"user":"alice"}' \
                 --location \
                 --insecure
            """;

        HttpRequestDefinition req = CurlParser.Parse(curl);

        Assert.Equal("POST", req.Method);
        Assert.Equal("https://api.example.com/login", req.Url);
        Assert.Equal("basic", req.Auth.Type);
        Assert.Equal("admin", req.Auth.Username);
        Assert.Equal("secret123", req.Auth.Password);
        Assert.True(req.TransportOptions.FollowRedirects);
        Assert.True(req.TransportOptions.DangerousIgnoreTlsErrors);
        Assert.NotNull(req.BodyDefinition);
        Assert.Equal("raw", req.BodyDefinition!.Mode);
        Assert.Equal("{\"user\":\"alice\"}", req.BodyDefinition.Text);
    }

    [Fact]
    public void PostmanImporter_ParsesHierarchyVariablesAndScripts()
    {
        string postmanJson = """
        {
            "info": {
                "_postman_id": "a0000000-0000-0000-0000-000000000001",
                "name": "E-Commerce API",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
            },
            "variable": [
                { "key": "baseUrl", "value": "https://api.store.com", "type": "string" },
                { "key": "apiKey", "value": "top-secret-token", "type": "secret" }
            ],
            "item": [
                {
                    "name": "Auth",
                    "item": [
                        {
                            "name": "Login",
                            "request": {
                                "method": "POST",
                                "url": {
                                    "raw": "{{baseUrl}}/auth/login",
                                    "query": [{ "key": "redirect", "value": "true" }]
                                },
                                "header": [
                                    { "key": "Content-Type", "value": "application/json" }
                                ],
                                "body": {
                                    "mode": "raw",
                                    "raw": "{\"username\":\"bob\"}"
                                }
                            },
                            "event": [
                                {
                                    "listen": "test",
                                    "script": {
                                        "exec": [
                                            "pm.test('Status is 200', function() { pm.response.to.have.status(200); });"
                                        ]
                                    }
                                }
                            ]
                        }
                    ]
                }
            ]
        }
        """;

        var result = PostmanImporter.ImportCollection(postmanJson);

        Assert.Equal("E-Commerce API", result.CollectionName);
        Assert.Equal(1, result.Counts.Requests);
        Assert.Equal(1, result.Counts.Folders);
        Assert.Equal(2, result.Counts.Variables);

        Assert.Single(result.DetectedSecrets);
        Assert.Equal("apiKey", result.DetectedSecrets[0].Key);

        var req = Assert.Single(result.Requests);
        Assert.Equal("Login", req.Name);
        Assert.Equal("POST", req.Method);
        Assert.NotNull(req.TestScript);
        Assert.Contains("pm.response.to.have.status(200)", req.TestScript);

        var folder = Assert.Single(result.Folders);
        Assert.Equal("Auth", folder.Name);
        Assert.Equal(req.Id, folder.RequestIds[0]);
    }

    [Fact]
    public void PostmanExporter_RoundTrip_MaintainsFidelity()
    {
        string initialPostmanJson = """
        {
            "info": {
                "_postman_id": "b0000000-0000-0000-0000-000000000002",
                "name": "RoundTrip Collection",
                "schema": "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
            },
            "item": [
                {
                    "name": "Orders",
                    "item": [
                        {
                            "name": "Get Order",
                            "request": {
                                "method": "GET",
                                "url": {
                                    "raw": "https://orders.test/orders/123",
                                    "query": [{ "key": "details", "value": "full" }]
                                },
                                "header": [
                                    { "key": "X-Client", "value": "Wrangler" }
                                ]
                            },
                            "event": [
                                {
                                    "listen": "prerequest",
                                    "script": { "exec": ["console.log('prep');"] }
                                },
                                {
                                    "listen": "test",
                                    "script": { "exec": ["pm.test('OK', () => pm.response.to.be.ok);"] }
                                }
                            ]
                        }
                    ]
                }
            ]
        }
        """;

        // 1. Import
        var importResult = PostmanImporter.ImportCollection(initialPostmanJson);

        // 2. Export
        var (exportedJson, report) = PostmanExporter.ExportCollection(
            importResult.CollectionName,
            importResult.CollectionId,
            importResult.Folders,
            importResult.Requests,
            importResult.Variables
        );

        Assert.Equal(1, report.ExportedRequests);
        Assert.Equal(1, report.ExportedFolders);

        // 3. Re-import exported JSON
        var reimported = PostmanImporter.ImportCollection(exportedJson);

        Assert.Equal(importResult.CollectionName, reimported.CollectionName);
        Assert.Equal(importResult.Requests.Count, reimported.Requests.Count);
        Assert.Equal(importResult.Folders.Count, reimported.Folders.Count);

        var origReq = importResult.Requests[0];
        var roundTripReq = reimported.Requests[0];

        Assert.Equal(origReq.Name, roundTripReq.Name);
        Assert.Equal(origReq.Method, roundTripReq.Method);
        Assert.Equal(origReq.Url, roundTripReq.Url);
        Assert.Equal(origReq.PreRequestScript, roundTripReq.PreRequestScript);
        Assert.Equal(origReq.TestScript, roundTripReq.TestScript);
        Assert.Equal(origReq.Headers[0].Name, roundTripReq.Headers[0].Name);
        Assert.Equal(origReq.Headers[0].Value, roundTripReq.Headers[0].Value);
    }

    [Fact]
    public void PostmanEnvironment_Export_RedactsSecretsAndProvidesReport()
    {
        string envJson = """
        {
            "id": "c0000000-0000-0000-0000-000000000003",
            "name": "Production Env",
            "values": [
                { "key": "host", "value": "prod.api.com", "type": "default" },
                { "key": "apiKey", "value": "super-secret-key-999", "type": "secret" }
            ]
        }
        """;

        var importResult = PostmanImporter.ImportEnvironment(envJson);
        Assert.Equal("Production Env", importResult.EnvironmentName);
        Assert.Single(importResult.DetectedSecrets);

        var (exportedJson, report) = PostmanExporter.ExportEnvironment(
            importResult.EnvironmentName,
            importResult.EnvironmentId,
            importResult.Variables,
            redactSecrets: true
        );

        Assert.Single(report.RedactedSecrets);
        Assert.Equal("apiKey", report.RedactedSecrets[0]);

        // Verify the secret value in JSON is empty string
        using var doc = JsonDocument.Parse(exportedJson);
        var values = doc.RootElement.GetProperty("values").EnumerateArray().ToList();
        var apiKeyValue = values.Single(v => v.GetProperty("key").GetString() == "apiKey");
        Assert.Equal("", apiKeyValue.GetProperty("value").GetString());
    }

    [Fact]
    public void OpenApiImporter_ParsesPathsTagsAndParameters()
    {
        string openApiJson = """
        {
            "openapi": "3.0.1",
            "info": {
                "title": "Pet Store API",
                "version": "1.0.0"
            },
            "servers": [
                { "url": "https://petstore.example.com/v1" }
            ],
            "paths": {
                "/pets": {
                    "get": {
                        "tags": ["Pets"],
                        "summary": "List all pets",
                        "parameters": [
                            { "name": "limit", "in": "query", "example": "10" }
                        ]
                    },
                    "post": {
                        "tags": ["Pets"],
                        "summary": "Create a pet",
                        "requestBody": {
                            "content": {
                                "application/json": {
                                    "example": "{\"name\":\"Fido\",\"tag\":\"dog\"}"
                                }
                            }
                        }
                    }
                }
            }
        }
        """;

        var result = OpenApiImporter.Import(openApiJson);

        Assert.Equal("Pet Store API", result.CollectionName);
        Assert.Equal(2, result.Requests.Count);
        Assert.Single(result.Folders);
        Assert.Equal("Pets", result.Folders[0].Name);
        Assert.Single(result.Variables);
        Assert.Equal("baseUrl", result.Variables[0].Name);

        var getReq = result.Requests.Single(r => r.Method == "GET");
        Assert.Equal("List all pets", getReq.Name);
        Assert.Single(getReq.Query);
        Assert.Equal("limit", getReq.Query[0].Name);

        var postReq = result.Requests.Single(r => r.Method == "POST");
        Assert.Equal("Create a pet", postReq.Name);
        Assert.NotNull(postReq.BodyDefinition);
        Assert.Equal("{\"name\":\"Fido\",\"tag\":\"dog\"}", postReq.BodyDefinition!.Text);
    }

    [Fact]
    public void HarImporter_ParsesEntriesIntoRequests()
    {
        string harJson = """
        {
            "log": {
                "version": "1.2",
                "pages": [
                    { "title": "Dashboard Page" }
                ],
                "entries": [
                    {
                        "request": {
                            "method": "GET",
                            "url": "https://dashboard.example.com/api/summary?org=42",
                            "headers": [
                                { "name": "X-Requested-With", "value": "XMLHttpRequest" }
                            ],
                            "queryString": [
                                { "name": "org", "value": "42" }
                            ]
                        }
                    },
                    {
                        "request": {
                            "method": "POST",
                            "url": "https://dashboard.example.com/api/metrics",
                            "headers": [
                                { "name": "Content-Type", "value": "application/json" }
                            ],
                            "postData": {
                                "mimeType": "application/json",
                                "text": "{\"view\":\"overview\"}"
                            }
                        }
                    }
                ]
            }
        }
        """;

        var result = HarImporter.Import(harJson);

        Assert.Equal("HAR: Dashboard Page", result.CollectionName);
        Assert.Equal(2, result.Requests.Count);

        var getReq = result.Requests[0];
        Assert.Equal("GET", getReq.Method);
        Assert.Equal("https://dashboard.example.com/api/summary?org=42", getReq.Url);
        Assert.Single(getReq.Headers);
        Assert.Single(getReq.Query);

        var postReq = result.Requests[1];
        Assert.Equal("POST", postReq.Method);
        Assert.NotNull(postReq.BodyDefinition);
        Assert.Equal("{\"view\":\"overview\"}", postReq.BodyDefinition!.Text);
    }

    [Fact]
    public async Task ProjectImportService_PreviewAndPublish_AtomicallyCreatesAssetsAndReport()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "awr-import-test-" + Guid.NewGuid().ToString("N"));
        IProjectSession? session = null;
        try
        {
            session = await CreateInitializedProjectSessionAsync(tempDir);

            string curl = "curl -X POST https://api.sample.com/items -H 'Content-Type: application/json' -d '{\"title\":\"New Item\"}'";

            // 1. Preview
            var preview = ProjectImportService.Preview(curl);
            Assert.True(preview.IsValid);
            Assert.Equal(ImportSourceFormat.Curl, preview.DetectedFormat);
            Assert.Equal(1, preview.Counts.Requests);
            Assert.Equal(2, preview.ProposedAssets.Count); // Collection + Request

            // 2. Publish
            var report = await ProjectImportService.PublishAsync(session, preview);

            Assert.Equal(1, report.Counts.Requests);
            Assert.Equal(2, report.WrittenAssetPaths.Count);
            Assert.NotNull(report.ReportFilePath);
            Assert.True(File.Exists(Path.Combine(tempDir, report.ReportFilePath!)));

            // 3. Verify session can read imported assets
            var view = await session.ReadAsync();
            Assert.True(view.IsValid, string.Join("; ", view.Diagnostics.Select(d => d.Message)));
            Assert.Contains(view.Assets, a => a.Kind == AssetKind.Collection && a.Name == "cURL Imports");
            Assert.Contains(view.Assets, a => a.Kind == AssetKind.Request && a.Path.Contains("curl-imports"));
        }
        finally
        {
            if (session != null) await session.DisposeAsync();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ProjectImportService_FailedImport_DoesNotCorruptOrOverwriteProject()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "awr-import-fail-" + Guid.NewGuid().ToString("N"));
        IProjectSession? session = null;
        try
        {
            session = await CreateInitializedProjectSessionAsync(tempDir);
            var initialView = await session.ReadAsync();
            int initialAssetCount = initialView.Assets.Count;

            // Attempt to publish an invalid preview with missing fields or corrupt content
            var corruptAsset = new ImportAssetPreview(
                Path: "collections/broken/collection.json",
                Kind: AssetKind.Collection,
                Name: "Broken",
                Content: new JsonObject { ["schemaVersion"] = 1 } // Missing required id, name, requestIds
            );

            var invalidPreview = new ImportPreview(
                DetectedFormat: ImportSourceFormat.PostmanCollection,
                Counts: new ImportedEntityCounts(0, 1, 0, 0, 0),
                DetectedSecrets: Array.Empty<SecretExtractionGuidance>(),
                UnsupportedFeatures: Array.Empty<string>(),
                ProposedAssets: [corruptAsset],
                ValidationErrors: Array.Empty<string>()
            );

            await Assert.ThrowsAnyAsync<Exception>(() => ProjectImportService.PublishAsync(session, invalidPreview));

            // Verify project assets were preserved exactly
            var postView = await session.ReadAsync();
            Assert.True(postView.IsValid);
            Assert.Equal(initialAssetCount, postView.Assets.Count);
            Assert.DoesNotContain(postView.Assets, a => a.Path.Contains("broken"));
        }
        finally
        {
            if (session != null) await session.DisposeAsync();
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
