using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed class ContractDiffEngine
{
    private static readonly string[] StandardHttpMethods = ["get", "post", "put", "delete", "patch", "head", "options"];

    public ContractDiffReport CompareOpenApiContracts(string baselineJson, string targetJson)
    {
        using var baseDoc = JsonDocument.Parse(baselineJson);
        using var targetDoc = JsonDocument.Parse(targetJson);

        var baseRoot = baseDoc.RootElement;
        var targetRoot = targetDoc.RootElement;

        string baseTitle = baseRoot.TryGetProperty("info", out var bi) && bi.TryGetProperty("title", out var bt) ? bt.GetString() ?? "Baseline API" : "Baseline API";
        string targetTitle = targetRoot.TryGetProperty("info", out var ti) && ti.TryGetProperty("title", out var tt) ? tt.GetString() ?? "Target API" : "Target API";

        var changes = new List<ContractBreakingChange>();

        var basePaths = baseRoot.TryGetProperty("paths", out var bp) && bp.ValueKind == JsonValueKind.Object ? bp : default;
        var targetPaths = targetRoot.TryGetProperty("paths", out var tp) && tp.ValueKind == JsonValueKind.Object ? tp : default;

        if (basePaths.ValueKind != JsonValueKind.Object)
        {
            return new ContractDiffReport(baseTitle, targetTitle, 0, 0, Array.Empty<ContractBreakingChange>());
        }

        foreach (var pathProp in basePaths.EnumerateObject())
        {
            string path = pathProp.Name;

            // 1. Removed Endpoint
            if (targetPaths.ValueKind != JsonValueKind.Object || !targetPaths.TryGetProperty(path, out var targetPathElem))
            {
                changes.Add(new ContractBreakingChange(
                    Category: "RemovedEndpoint",
                    Severity: "Breaking",
                    Path: path,
                    Method: null,
                    Description: $"Endpoint '{path}' was removed from the API specification."
                ));
                continue;
            }

            var baseMethods = pathProp.Value;
            foreach (var method in StandardHttpMethods)
            {
                if (baseMethods.TryGetProperty(method, out var baseOp))
                {
                    // 2. Removed Method
                    if (!targetPathElem.TryGetProperty(method, out var targetOp))
                    {
                        changes.Add(new ContractBreakingChange(
                            Category: "RemovedMethod",
                            Severity: "Breaking",
                            Path: path,
                            Method: method.ToUpperInvariant(),
                            Description: $"HTTP method '{method.ToUpperInvariant()}' was removed from endpoint '{path}'."
                        ));
                        continue;
                    }

                    // 3. Compare Parameters
                    CompareParameters(path, method.ToUpperInvariant(), baseOp, targetOp, changes);

                    // 4. Compare Request Body
                    CompareRequestBody(path, method.ToUpperInvariant(), baseOp, targetOp, changes);

                    // 5. Compare Responses
                    CompareResponses(path, method.ToUpperInvariant(), baseOp, targetOp, changes);
                }
            }
        }

        int breakingCount = changes.Count(c => c.Severity == "Breaking");
        int nonBreakingCount = changes.Count(c => c.Severity != "Breaking");

        return new ContractDiffReport(
            BaselineTitle: baseTitle,
            TargetTitle: targetTitle,
            TotalBreakingChanges: breakingCount,
            TotalNonBreakingChanges: nonBreakingCount,
            Changes: changes
        );
    }

    private static void CompareParameters(string path, string method, JsonElement baseOp, JsonElement targetOp, List<ContractBreakingChange> changes)
    {
        var baseParams = baseOp.TryGetProperty("parameters", out var bp) && bp.ValueKind == JsonValueKind.Array ? bp.EnumerateArray().ToList() : new();
        var targetParams = targetOp.TryGetProperty("parameters", out var tp) && tp.ValueKind == JsonValueKind.Array ? tp.EnumerateArray().ToList() : new();

        var targetParamMap = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in targetParams)
        {
            if (p.TryGetProperty("name", out var n) && n.GetString() is { } name)
            {
                string inLoc = p.TryGetProperty("in", out var i) && i.GetString() is { } loc ? loc : "query";
                targetParamMap[$"{inLoc}:{name}"] = p;
            }
        }

        foreach (var p in baseParams)
        {
            if (p.TryGetProperty("name", out var n) && n.GetString() is { } name)
            {
                string inLoc = p.TryGetProperty("in", out var i) && i.GetString() is { } loc ? loc : "query";
                string key = $"{inLoc}:{name}";
                bool baseReq = p.TryGetProperty("required", out var r) && r.GetBoolean();

                if (!targetParamMap.TryGetValue(key, out var tpElem))
                {
                    if (baseReq)
                    {
                        changes.Add(new ContractBreakingChange(
                            Category: "ChangedParameter",
                            Severity: "Breaking",
                            Path: path,
                            Method: method,
                            Description: $"Required parameter '{name}' ({inLoc}) was removed."
                        ));
                    }
                }
                else
                {
                    // Check type change
                    string? baseType = GetParamType(p);
                    string? targetType = GetParamType(tpElem);
                    if (baseType != null && targetType != null && !string.Equals(baseType, targetType, StringComparison.OrdinalIgnoreCase))
                    {
                        changes.Add(new ContractBreakingChange(
                            Category: "ChangedParameter",
                            Severity: "Breaking",
                            Path: path,
                            Method: method,
                            Description: $"Parameter '{name}' type changed from '{baseType}' to '{targetType}'."
                        ));
                    }
                }
            }
        }

        // Check newly required parameters
        foreach (var (k, tpElem) in targetParamMap)
        {
            bool targetReq = tpElem.TryGetProperty("required", out var tr) && tr.GetBoolean();
            if (targetReq)
            {
                var baseMatch = baseParams.FirstOrDefault(bp =>
                {
                    string n = bp.TryGetProperty("name", out var bn) ? bn.GetString() ?? "" : "";
                    string loc = bp.TryGetProperty("in", out var bi) ? bi.GetString() ?? "query" : "query";
                    return string.Equals($"{loc}:{n}", k, StringComparison.OrdinalIgnoreCase);
                });

                if (baseMatch.ValueKind == JsonValueKind.Undefined || (!baseMatch.TryGetProperty("required", out var br) || !br.GetBoolean()))
                {
                    string name = tpElem.TryGetProperty("name", out var n) ? n.GetString() ?? k : k;
                    changes.Add(new ContractBreakingChange(
                        Category: "NewRequiredField",
                        Severity: "Breaking",
                        Path: path,
                        Method: method,
                        Description: $"Newly required parameter '{name}' was introduced."
                    ));
                }
            }
        }
    }

    private static string? GetParamType(JsonElement p)
    {
        if (p.TryGetProperty("schema", out var s) && s.TryGetProperty("type", out var t))
        {
            return t.GetString();
        }
        if (p.TryGetProperty("type", out var pt))
        {
            return pt.GetString();
        }
        return null;
    }

    private static void CompareRequestBody(string path, string method, JsonElement baseOp, JsonElement targetOp, List<ContractBreakingChange> changes)
    {
        bool baseReqBody = baseOp.TryGetProperty("requestBody", out var brb) && brb.TryGetProperty("required", out var br) && br.GetBoolean();
        bool targetReqBody = targetOp.TryGetProperty("requestBody", out var trb) && trb.TryGetProperty("required", out var tr) && tr.GetBoolean();

        if (!baseReqBody && targetReqBody)
        {
            changes.Add(new ContractBreakingChange(
                Category: "NewRequiredField",
                Severity: "Breaking",
                Path: path,
                Method: method,
                Description: "Request body is now required where it was previously optional."
            ));
        }
    }

    private static void CompareResponses(string path, string method, JsonElement baseOp, JsonElement targetOp, List<ContractBreakingChange> changes)
    {
        if (baseOp.TryGetProperty("responses", out var bResp) && bResp.ValueKind == JsonValueKind.Object)
        {
            var tResp = targetOp.TryGetProperty("responses", out var tr) && tr.ValueKind == JsonValueKind.Object ? tr : default;
            foreach (var respProp in bResp.EnumerateObject())
            {
                string statusCode = respProp.Name;
                if ((statusCode.StartsWith("2") || statusCode == "default") && tResp.ValueKind == JsonValueKind.Object)
                {
                    if (!tResp.TryGetProperty(statusCode, out _))
                    {
                        changes.Add(new ContractBreakingChange(
                            Category: "RemovedResponseField",
                            Severity: "Breaking",
                            Path: path,
                            Method: method,
                            Description: $"Successful response code '{statusCode}' was removed from endpoint."
                        ));
                    }
                }
            }
        }
    }
}

public sealed class DocumentationGenerator
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization", "Cookie", "Set-Cookie", "X-Api-Key", "ApiKey", "Token", "Secret"
    };

    public string GenerateMarkdownDocs(
        string projectTitle,
        IReadOnlyList<HttpRequestDefinition> requests,
        IReadOnlyList<ContractOperationLink>? links = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {projectTitle} — API Documentation");
        sb.AppendLine();
        sb.AppendLine("Auto-generated documentation. Sensitive credentials and secrets are omitted.");
        sb.AppendLine();

        var linkMap = links?.ToDictionary(l => l.RequestId, l => l) ?? new();

        foreach (var req in requests)
        {
            sb.AppendLine($"## {req.Name}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(req.Description))
            {
                sb.AppendLine(req.Description);
                sb.AppendLine();
            }

            sb.AppendLine($"**Method:** `{req.Method.ToUpperInvariant()}`  ");
            sb.AppendLine($"**URL:** `{req.Url}`  ");

            if (linkMap.TryGetValue(req.Id, out var link))
            {
                sb.AppendLine($"**Contract Operation:** `{link.OperationId}` (`{link.Path}`)  ");
            }

            sb.AppendLine();

            // Authentication Instructions (Without secret values!)
            sb.AppendLine("### Authentication");
            if (req.Auth is not null && !string.Equals(req.Auth.Type, "none", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"Authentication type: `{req.Auth.Type}`. Supply valid credentials via environment variables or secret vault; secret values are omitted from documentation.");
            }
            else
            {
                sb.AppendLine("No authentication required.");
            }
            sb.AppendLine();

            // Headers (Redacted)
            if (req.Headers is { Count: > 0 })
            {
                sb.AppendLine("### Headers");
                sb.AppendLine("| Header | Description |");
                sb.AppendLine("|---|---|");
                foreach (var h in req.Headers.Where(h => h.Enabled))
                {
                    string displayVal = SensitiveHeaders.Contains(h.Name) ? "[REDACTED_SECRET]" : h.Value;
                    sb.AppendLine($"| `{h.Name}` | Value: `{displayVal}` |");
                }
                sb.AppendLine();
            }

            // Body
            if (!string.IsNullOrEmpty(req.Body))
            {
                sb.AppendLine("### Request Body");
                sb.AppendLine("```json");
                sb.AppendLine(req.Body);
                sb.AppendLine("```");
                sb.AppendLine();
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string GenerateHtmlDocs(
        string projectTitle,
        IReadOnlyList<HttpRequestDefinition> requests,
        IReadOnlyList<ContractOperationLink>? links = null)
    {
        string md = GenerateMarkdownDocs(projectTitle, requests, links);
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"UTF-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
        sb.AppendLine($"  <title>{projectTitle} — API Documentation</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; line-height: 1.6; max-width: 900px; margin: 0 auto; padding: 2rem; color: #24292e; }");
        sb.AppendLine("    pre { background: #f6f8fa; padding: 1rem; border-radius: 6px; overflow-x: auto; }");
        sb.AppendLine("    code { background: #f6f8fa; padding: 0.2rem 0.4rem; border-radius: 3px; font-family: monospace; }");
        sb.AppendLine("    table { width: 100%; border-collapse: collapse; margin: 1rem 0; }");
        sb.AppendLine("    th, td { border: 1px solid #e1e4e8; padding: 8px 12px; text-align: left; }");
        sb.AppendLine("    th { background: #f6f8fa; }");
        sb.AppendLine("    .method { display: inline-block; padding: 2px 6px; font-weight: bold; border-radius: 4px; color: #fff; background: #0366d6; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        // Convert basic markdown headers and tables to html
        sb.AppendLine($"<h1>{projectTitle} — API Documentation</h1>");
        sb.AppendLine("<p><em>Static documentation generated by API Wrangler. Sensitive secret values are omitted.</em></p>");
        sb.AppendLine("<hr>");

        foreach (var req in requests)
        {
            sb.AppendLine($"<h2>{req.Name}</h2>");
            if (!string.IsNullOrEmpty(req.Description))
            {
                sb.AppendLine($"<p>{req.Description}</p>");
            }
            sb.AppendLine($"<p><span class=\"method\">{req.Method}</span> <code>{req.Url}</code></p>");

            if (req.Auth is not null && !string.Equals(req.Auth.Type, "none", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"<p><strong>Authentication:</strong> <code>{req.Auth.Type}</code>. Supply credentials via environment variables or secret vault; secret values are omitted from documentation.</p>");
            }

            if (req.Headers is { Count: > 0 })
            {
                sb.AppendLine("<h4>Headers</h4><table><tr><th>Header</th><th>Value</th></tr>");
                foreach (var h in req.Headers.Where(h => h.Enabled))
                {
                    string displayVal = SensitiveHeaders.Contains(h.Name) ? "[REDACTED_SECRET]" : h.Value;
                    sb.AppendLine($"<tr><td><code>{h.Name}</code></td><td><code>{displayVal}</code></td></tr>");
                }
                sb.AppendLine("</table>");
            }

            if (!string.IsNullOrEmpty(req.Body))
            {
                sb.AppendLine("<h4>Request Body</h4>");
                sb.AppendLine($"<pre><code>{req.Body}</code></pre>");
            }
            sb.AppendLine("<hr>");
        }

        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    public void PublishDocumentation(
        string outputDirectory,
        string projectTitle,
        IReadOnlyList<HttpRequestDefinition> requests,
        IReadOnlyList<ContractOperationLink>? links = null)
    {
        Directory.CreateDirectory(outputDirectory);
        string md = GenerateMarkdownDocs(projectTitle, requests, links);
        string html = GenerateHtmlDocs(projectTitle, requests, links);

        File.WriteAllText(Path.Combine(outputDirectory, "api-reference.md"), md, Encoding.UTF8);
        File.WriteAllText(Path.Combine(outputDirectory, "index.html"), html, Encoding.UTF8);
    }
}

public sealed class ApiCatalogIndex
{
    private readonly List<CatalogItem> items = new();

    private sealed record CatalogItem(
        Guid RequestId,
        string Name,
        string Method,
        string Url,
        string Path,
        string? CollectionName,
        string LowerName,
        string LowerPath,
        string CombinedTokens
    );

    public int TotalIndexed => items.Count;

    public void Clear() => items.Clear();

    public void IndexRequests(IEnumerable<HttpRequestDefinition> requests, string? collectionName = null)
    {
        foreach (var r in requests)
        {
            string path = ExtractPath(r.Url);
            string lowerName = r.Name.ToLowerInvariant();
            string lowerPath = path.ToLowerInvariant();
            string tokens = $"{lowerName} {r.Method.ToLowerInvariant()} {r.Url.ToLowerInvariant()} {lowerPath} {(collectionName ?? "").ToLowerInvariant()}";

            items.Add(new CatalogItem(
                RequestId: r.Id,
                Name: r.Name,
                Method: r.Method.ToUpperInvariant(),
                Url: r.Url,
                Path: path,
                CollectionName: collectionName,
                LowerName: lowerName,
                LowerPath: lowerPath,
                CombinedTokens: tokens
            ));
        }
    }

    public IReadOnlyList<CatalogSearchResult> Search(string query, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return items.Take(limit).Select(i => new CatalogSearchResult(
                RequestId: i.RequestId,
                Name: i.Name,
                Method: i.Method,
                Url: i.Url,
                CollectionName: i.CollectionName,
                Score: 1.0,
                MatchedField: "Default"
            )).ToList();
        }

        string q = query.Trim().ToLowerInvariant();
        var results = new List<CatalogSearchResult>();

        foreach (var item in items)
        {
            if (!item.CombinedTokens.Contains(q, StringComparison.Ordinal)) continue;

            double score = 20;
            string matchedField = "Tokens";

            if (item.LowerName.Contains(q, StringComparison.Ordinal))
            {
                score = item.LowerName.StartsWith(q, StringComparison.Ordinal) ? 150 : 100;
                matchedField = "Name";
            }
            else if (item.LowerPath.Contains(q, StringComparison.Ordinal))
            {
                score = item.LowerPath.StartsWith(q, StringComparison.Ordinal) ? 110 : 80;
                matchedField = "Path";
            }

            results.Add(new CatalogSearchResult(
                RequestId: item.RequestId,
                Name: item.Name,
                Method: item.Method,
                Url: item.Url,
                CollectionName: item.CollectionName,
                Score: score,
                MatchedField: matchedField
            ));
        }

        results.Sort((a, b) => b.Score.CompareTo(a.Score));
        return results.Take(limit).ToList();
    }

    private static string ExtractPath(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "/";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.AbsolutePath;
        int idx = url.IndexOf('?');
        return idx >= 0 ? url[..idx] : url;
    }
}

public sealed class SdkGenerator
{
    public SdkGenerationResult GenerateSdk(
        IReadOnlyList<HttpRequestDefinition> requests,
        SdkGenerationTarget target,
        string previewDirectory)
    {
        Directory.CreateDirectory(previewDirectory);
        var files = new List<GeneratedSdkFile>();

        switch (target)
        {
            case SdkGenerationTarget.CSharp:
                files.Add(GenerateCSharpSdk(requests));
                break;
            case SdkGenerationTarget.TypeScript:
                files.Add(GenerateTypeScriptSdk(requests));
                break;
            case SdkGenerationTarget.Python:
                files.Add(GeneratePythonSdk(requests));
                break;
        }

        // Write files to preview directory
        foreach (var f in files)
        {
            string fullPath = Path.Combine(previewDirectory, f.RelativePath);
            string? dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(fullPath, f.Content, Encoding.UTF8);
        }

        return new SdkGenerationResult(target, previewDirectory, files);
    }

    private static GeneratedSdkFile GenerateCSharpSdk(IReadOnlyList<HttpRequestDefinition> requests)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Net.Http;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine("using System.Threading;");
        sb.AppendLine("using System.Threading.Tasks;");
        sb.AppendLine();
        sb.AppendLine("namespace ApiWrangler.GeneratedClient;");
        sb.AppendLine();
        sb.AppendLine("public sealed class ApiClient");
        sb.AppendLine("{");
        sb.AppendLine("    private readonly HttpClient httpClient;");
        sb.AppendLine();
        sb.AppendLine("    public ApiClient(HttpClient httpClient) => this.httpClient = httpClient;");
        sb.AppendLine();

        foreach (var req in requests)
        {
            string methodName = SanitizeIdentifier($"{req.Method}_{req.Name}");
            sb.AppendLine($"    /// <summary>{req.Name}: {req.Method} {req.Url}</summary>");
            sb.AppendLine($"    public async Task<HttpResponseMessage> {methodName}Async(string? body = null, CancellationToken cancellationToken = default)");
            sb.AppendLine("    {");
            sb.AppendLine($"        using var request = new HttpRequestMessage(HttpMethod.{req.Method.ToUpperInvariant()}, \"{req.Url}\");");
            if (!string.IsNullOrEmpty(req.Body))
            {
                sb.AppendLine("        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, \"application/json\");");
            }
            sb.AppendLine("        return await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);");
            sb.AppendLine("    }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return new GeneratedSdkFile("ApiClient.cs", sb.ToString());
    }

    private static GeneratedSdkFile GenerateTypeScriptSdk(IReadOnlyList<HttpRequestDefinition> requests)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated />");
        sb.AppendLine("export class ApiClient {");
        sb.AppendLine("  constructor(private baseUrl: string = '', private fetchFn = fetch) {}");
        sb.AppendLine();

        foreach (var req in requests)
        {
            string methodName = SanitizeIdentifierCamel($"{req.Method}_{req.Name}");
            sb.AppendLine($"  /** {req.Name}: {req.Method} {req.Url} */");
            sb.AppendLine($"  async {methodName}(body?: string, init?: RequestInit): Promise<Response> {{");
            sb.AppendLine($"    return this.fetchFn(`{req.Url}`, {{");
            sb.AppendLine($"      method: '{req.Method.ToUpperInvariant()}',");
            sb.AppendLine("      body,");
            sb.AppendLine("      ...init");
            sb.AppendLine("    });");
            sb.AppendLine("  }");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return new GeneratedSdkFile("apiClient.ts", sb.ToString());
    }

    private static GeneratedSdkFile GeneratePythonSdk(IReadOnlyList<HttpRequestDefinition> requests)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# <auto-generated />");
        sb.AppendLine("import requests");
        sb.AppendLine();
        sb.AppendLine("class ApiClient:");
        sb.AppendLine("    def __init__(self, base_url: str = ''):");
        sb.AppendLine("        self.base_url = base_url");
        sb.AppendLine("        self.session = requests.Session()");
        sb.AppendLine();

        foreach (var req in requests)
        {
            string methodName = SanitizeIdentifierSnake($"{req.Method}_{req.Name}");
            sb.AppendLine($"    def {methodName}(self, data=None, **kwargs):");
            sb.AppendLine($"        \"\"\"{req.Name}: {req.Method} {req.Url}\"\"\"");
            sb.AppendLine($"        return self.session.request('{req.Method.ToUpperInvariant()}', f'{req.Url}', data=data, **kwargs)");
            sb.AppendLine();
        }

        return new GeneratedSdkFile("api_client.py", sb.ToString());
    }

    private static string SanitizeIdentifier(string name)
    {
        var clean = Regex.Replace(name, @"[^a-zA-Z0-9_]", "_");
        if (char.IsDigit(clean[0])) clean = "_" + clean;
        return clean;
    }

    private static string SanitizeIdentifierCamel(string name)
    {
        string id = SanitizeIdentifier(name);
        return char.ToLowerInvariant(id[0]) + id[1..];
    }

    private static string SanitizeIdentifierSnake(string name)
    {
        return Regex.Replace(name, @"[^a-zA-Z0-9_]", "_").ToLowerInvariant();
    }
}
