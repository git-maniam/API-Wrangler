using System.Text.Json;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed record OpenApiImportResult(
    string CollectionName,
    Guid CollectionId,
    string? BaseUrl,
    IReadOnlyList<HttpRequestDefinition> Requests,
    IReadOnlyList<ImportedFolder> Folders,
    IReadOnlyList<VariableEntry> Variables,
    IReadOnlyList<string> UnsupportedFeatures,
    ImportedEntityCounts Counts
);

public static class OpenApiImporter
{
    private static readonly string[] HttpMethods = ["get", "post", "put", "delete", "patch", "head", "options", "trace"];

    public static OpenApiImportResult Import(string jsonContent)
    {
        using var doc = JsonDocument.Parse(jsonContent);
        JsonElement root = doc.RootElement;

        var unsupportedFeatures = new List<string>();

        // Verify OpenAPI version
        string openApiVersion = "3.0.0";
        if (root.TryGetProperty("openapi", out var v) && v.GetString() is { } ver)
        {
            openApiVersion = ver;
        }
        else if (root.TryGetProperty("swagger", out var sw) && sw.GetString() is { } swVer)
        {
            openApiVersion = swVer;
            unsupportedFeatures.Add($"Swagger 2.0 detected ({swVer}); parsed with OpenAPI 3 compatibility.");
        }

        string collectionName = "OpenAPI Specification";
        if (root.TryGetProperty("info", out var info) && info.TryGetProperty("title", out var title) && title.GetString() is { } t)
        {
            collectionName = t;
        }

        Guid collectionId = Guid.NewGuid();

        // Servers
        string? baseUrl = null;
        if (root.TryGetProperty("servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
        {
            foreach (var server in servers.EnumerateArray())
            {
                if (server.TryGetProperty("url", out var sUrl) && sUrl.GetString() is { } sUrlStr)
                {
                    baseUrl = sUrlStr;
                    break;
                }
            }
        }

        var variables = new List<VariableEntry>();
        if (!string.IsNullOrEmpty(baseUrl))
        {
            variables.Add(new VariableEntry("baseUrl", baseUrl, VariableSource.Collection));
        }

        var foldersByTag = new Dictionary<string, ImportedFolder>(StringComparer.OrdinalIgnoreCase);
        var requests = new List<HttpRequestDefinition>();

        if (root.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
        {
            foreach (var pathProp in paths.EnumerateObject())
            {
                string pathPattern = pathProp.Name;
                JsonElement pathItem = pathProp.Value;

                foreach (var method in HttpMethods)
                {
                    if (pathItem.TryGetProperty(method, out var opElem) && opElem.ValueKind == JsonValueKind.Object)
                    {
                        string opMethod = method.ToUpperInvariant();
                        string? summary = opElem.TryGetProperty("summary", out var s) ? s.GetString() : null;
                        string? operationId = opElem.TryGetProperty("operationId", out var opId) ? opId.GetString() : null;
                        string? description = opElem.TryGetProperty("description", out var desc) ? desc.GetString() : null;

                        string reqName = summary ?? operationId ?? $"{opMethod} {pathPattern}";

                        // Folders by Tag
                        Guid? folderId = null;
                        if (opElem.TryGetProperty("tags", out var tagsElem) && tagsElem.ValueKind == JsonValueKind.Array)
                        {
                            var firstTag = tagsElem.EnumerateArray().FirstOrDefault();
                            if (firstTag.ValueKind == JsonValueKind.String && firstTag.GetString() is { } tagName && !string.IsNullOrWhiteSpace(tagName))
                            {
                                if (!foldersByTag.TryGetValue(tagName, out var folder))
                                {
                                    folder = new ImportedFolder(
                                        Id: Guid.NewGuid(),
                                        CollectionId: collectionId,
                                        ParentFolderId: null,
                                        Name: tagName,
                                        Description: null,
                                        RequestIds: new List<Guid>()
                                    );
                                    foldersByTag[tagName] = folder;
                                }
                                folderId = folder.Id;
                            }
                        }

                        // Parameters (query and header)
                        var queryParams = new List<QueryParamEntry>();
                        var headers = new List<HeaderEntry>();

                        if (opElem.TryGetProperty("parameters", out var paramsElem) && paramsElem.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var param in paramsElem.EnumerateArray())
                            {
                                string pName = param.TryGetProperty("name", out var pn) ? (pn.GetString() ?? "") : "";
                                string pIn = param.TryGetProperty("in", out var pi) ? (pi.GetString() ?? "query") : "query";
                                bool required = param.TryGetProperty("required", out var pr) && pr.GetBoolean();

                                string exampleVal = "";
                                if (param.TryGetProperty("example", out var ex))
                                {
                                    exampleVal = ex.ToString();
                                }
                                else if (param.TryGetProperty("schema", out var sch) && sch.TryGetProperty("default", out var defVal))
                                {
                                    exampleVal = defVal.ToString();
                                }

                                if (pIn == "query")
                                {
                                    queryParams.Add(new QueryParamEntry(pName, exampleVal, Enabled: true));
                                }
                                else if (pIn == "header")
                                {
                                    headers.Add(new HeaderEntry(pName, exampleVal, Enabled: true));
                                }
                                else if (pIn == "cookie")
                                {
                                    headers.Add(new HeaderEntry("Cookie", $"{pName}={exampleVal}", Enabled: true));
                                }
                            }
                        }

                        // Request Body
                        RequestBodyDefinition? bodyDef = null;
                        if (opElem.TryGetProperty("requestBody", out var rbElem) && rbElem.ValueKind == JsonValueKind.Object)
                        {
                            if (rbElem.TryGetProperty("content", out var contentElem) && contentElem.ValueKind == JsonValueKind.Object)
                            {
                                if (contentElem.TryGetProperty("application/json", out var jsonContentElem))
                                {
                                    string sampleJson = "{}";
                                    if (jsonContentElem.TryGetProperty("example", out var ex))
                                    {
                                        sampleJson = ex.ToString();
                                    }
                                    else if (jsonContentElem.TryGetProperty("schema", out var schemaElem))
                                    {
                                        sampleJson = GenerateSampleFromSchema(schemaElem);
                                    }

                                    headers.Add(new HeaderEntry("Content-Type", "application/json"));
                                    bodyDef = new RequestBodyDefinition { Mode = "raw", Text = sampleJson };
                                }
                                else
                                {
                                    var firstContent = contentElem.EnumerateObject().FirstOrDefault();
                                    if (firstContent.Value.ValueKind == JsonValueKind.Object)
                                    {
                                        headers.Add(new HeaderEntry("Content-Type", firstContent.Name));
                                        bodyDef = new RequestBodyDefinition { Mode = "raw", Text = "" };
                                    }
                                }
                            }
                        }

                        // URL construction
                        string effectiveUrl = string.IsNullOrEmpty(baseUrl)
                            ? $"{{baseUrl}}{pathPattern}"
                            : $"{baseUrl.TrimEnd('/')}/{pathPattern.TrimStart('/')}";

                        var req = new HttpRequestDefinition
                        {
                            Id = Guid.NewGuid(),
                            CollectionId = collectionId,
                            FolderId = folderId,
                            Name = reqName,
                            Description = description,
                            Method = opMethod,
                            Url = effectiveUrl,
                            Headers = headers,
                            Query = queryParams,
                            BodyDefinition = bodyDef
                        };

                        requests.Add(req);
                    }
                }
            }
        }

        // Reconcile folder RequestIds
        var finalizedFolders = new List<ImportedFolder>();
        foreach (var f in foldersByTag.Values)
        {
            var reqIds = requests.Where(r => r.FolderId == f.Id).Select(r => r.Id).ToList();
            finalizedFolders.Add(new ImportedFolder(
                Id: f.Id,
                CollectionId: f.CollectionId,
                ParentFolderId: f.ParentFolderId,
                Name: f.Name,
                Description: f.Description,
                RequestIds: reqIds
            ));
        }

        var counts = new ImportedEntityCounts(
            Requests: requests.Count,
            Collections: 1,
            Folders: finalizedFolders.Count,
            Environments: 0,
            Variables: variables.Count
        );

        return new OpenApiImportResult(
            CollectionName: collectionName,
            CollectionId: collectionId,
            BaseUrl: baseUrl,
            Requests: requests,
            Folders: finalizedFolders,
            Variables: variables,
            UnsupportedFeatures: unsupportedFeatures,
            Counts: counts
        );
    }

    private static string GenerateSampleFromSchema(JsonElement schema)
    {
        if (schema.TryGetProperty("example", out var ex)) return ex.ToString();
        if (schema.TryGetProperty("type", out var t) && t.GetString() == "object" &&
            schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            var sample = new Dictionary<string, object>();
            foreach (var prop in props.EnumerateObject())
            {
                sample[prop.Name] = "value";
            }
            return JsonSerializer.Serialize(sample);
        }
        return "{}";
    }
}
