using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed record ImportedEntityCounts(
    int Requests,
    int Collections,
    int Folders,
    int Environments,
    int Variables
);

public sealed record SecretExtractionGuidance(
    string Key,
    string EnvironmentOrCollectionName,
    string Reason,
    string RecommendedStorage = ".local/ local secrets store"
);

public sealed record PostmanImportResult(
    string CollectionName,
    Guid CollectionId,
    IReadOnlyList<HttpRequestDefinition> Requests,
    IReadOnlyList<ImportedFolder> Folders,
    IReadOnlyList<VariableEntry> Variables,
    IReadOnlyList<SecretExtractionGuidance> DetectedSecrets,
    IReadOnlyList<string> UnsupportedFeatures,
    ImportedEntityCounts Counts
);

public sealed record ImportedFolder(
    Guid Id,
    Guid CollectionId,
    Guid? ParentFolderId,
    string Name,
    string? Description,
    IReadOnlyList<Guid> RequestIds
);

public sealed record PostmanEnvironmentImportResult(
    string EnvironmentName,
    Guid EnvironmentId,
    IReadOnlyList<VariableEntry> Variables,
    IReadOnlyList<SecretExtractionGuidance> DetectedSecrets,
    int VariableCount
);

public static class PostmanImporter
{
    private static readonly HashSet<string> SensitiveKeySubstrings = new(StringComparer.OrdinalIgnoreCase)
    {
        "secret", "token", "password", "api_key", "apikey", "auth", "private", "access_key", "client_secret"
    };

    public static PostmanImportResult ImportCollection(string jsonContent)
    {
        using var doc = JsonDocument.Parse(jsonContent);
        JsonElement root = doc.RootElement;

        var unsupportedFeatures = new List<string>();
        var detectedSecrets = new List<SecretExtractionGuidance>();
        var compatibilityService = new PostmanCompatibilityService();

        // 1. Info
        string collectionName = "Imported Collection";
        Guid collectionId = Guid.NewGuid();
        if (root.TryGetProperty("info", out var info))
        {
            if (info.TryGetProperty("name", out var n) && n.GetString() is { } nameStr)
                collectionName = nameStr;
            if (info.TryGetProperty("_postman_id", out var idElem) && Guid.TryParse(idElem.GetString(), out var id))
                collectionId = id;
        }

        // 2. Collection variables
        var collectionVariables = new List<VariableEntry>();
        if (root.TryGetProperty("variable", out var variablesElem) && variablesElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in variablesElem.EnumerateArray())
            {
                string key = v.TryGetProperty("key", out var k) ? (k.GetString() ?? "") : "";
                string val = v.TryGetProperty("value", out var valElem) ? (valElem.GetString() ?? "") : "";
                string type = v.TryGetProperty("type", out var t) ? (t.GetString() ?? "string") : "string";

                bool isSecret = type.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
                                SensitiveKeySubstrings.Any(s => key.Contains(s, StringComparison.OrdinalIgnoreCase));

                if (isSecret)
                {
                    detectedSecrets.Add(new SecretExtractionGuidance(key, collectionName, $"Variable '{key}' appears to be sensitive (type: {type})."));
                }

                collectionVariables.Add(new VariableEntry(key, val, VariableSource.Collection, isSecret));
            }
        }

        // 3. Process items recursively
        var requests = new List<HttpRequestDefinition>();
        var folders = new List<ImportedFolder>();

        if (root.TryGetProperty("item", out var itemsElem) && itemsElem.ValueKind == JsonValueKind.Array)
        {
            ProcessItems(itemsElem, collectionId, null, requests, folders, unsupportedFeatures, compatibilityService);
        }

        var counts = new ImportedEntityCounts(
            Requests: requests.Count,
            Collections: 1,
            Folders: folders.Count,
            Environments: 0,
            Variables: collectionVariables.Count
        );

        return new PostmanImportResult(
            CollectionName: collectionName,
            CollectionId: collectionId,
            Requests: requests,
            Folders: folders,
            Variables: collectionVariables,
            DetectedSecrets: detectedSecrets,
            UnsupportedFeatures: unsupportedFeatures.Distinct().ToList(),
            Counts: counts
        );
    }

    public static PostmanEnvironmentImportResult ImportEnvironment(string jsonContent)
    {
        using var doc = JsonDocument.Parse(jsonContent);
        JsonElement root = doc.RootElement;

        string envName = "Imported Environment";
        Guid envId = Guid.NewGuid();

        if (root.TryGetProperty("name", out var n) && n.GetString() is { } nameStr)
            envName = nameStr;
        if (root.TryGetProperty("id", out var idElem) && Guid.TryParse(idElem.GetString(), out var id))
            envId = id;

        var variables = new List<VariableEntry>();
        var detectedSecrets = new List<SecretExtractionGuidance>();

        if (root.TryGetProperty("values", out var valuesElem) && valuesElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var valItem in valuesElem.EnumerateArray())
            {
                bool enabled = !valItem.TryGetProperty("enabled", out var en) || en.GetBoolean();
                if (!enabled) continue;

                string key = valItem.TryGetProperty("key", out var k) ? (k.GetString() ?? "") : "";
                string value = valItem.TryGetProperty("value", out var v) ? (v.GetString() ?? "") : "";
                string type = valItem.TryGetProperty("type", out var t) ? (t.GetString() ?? "default") : "default";

                bool isSecret = type.Equals("secret", StringComparison.OrdinalIgnoreCase) ||
                                SensitiveKeySubstrings.Any(s => key.Contains(s, StringComparison.OrdinalIgnoreCase));

                if (isSecret)
                {
                    detectedSecrets.Add(new SecretExtractionGuidance(key, envName, $"Variable '{key}' contains secret or sensitive token."));
                }

                variables.Add(new VariableEntry(key, value, VariableSource.Environment, isSecret));
            }
        }

        return new PostmanEnvironmentImportResult(
            EnvironmentName: envName,
            EnvironmentId: envId,
            Variables: variables,
            DetectedSecrets: detectedSecrets,
            VariableCount: variables.Count
        );
    }

    private static void ProcessItems(
        JsonElement itemsElem,
        Guid collectionId,
        Guid? parentFolderId,
        List<HttpRequestDefinition> requests,
        List<ImportedFolder> folders,
        List<string> unsupportedFeatures,
        PostmanCompatibilityService compatibilityService)
    {
        foreach (JsonElement item in itemsElem.EnumerateArray())
        {
            string itemName = item.TryGetProperty("name", out var n) ? (n.GetString() ?? "Item") : "Item";
            string? description = item.TryGetProperty("description", out var d) ? d.GetString() : null;

            // If it has "item" array, it is a Folder
            if (item.TryGetProperty("item", out var subItems) && subItems.ValueKind == JsonValueKind.Array)
            {
                Guid folderId = Guid.NewGuid();
                var folderRequestIds = new List<Guid>();

                int requestStartIndex = requests.Count;
                ProcessItems(subItems, collectionId, folderId, requests, folders, unsupportedFeatures, compatibilityService);
                for (int i = requestStartIndex; i < requests.Count; i++)
                {
                    if (requests[i].FolderId == folderId)
                    {
                        folderRequestIds.Add(requests[i].Id);
                    }
                }

                folders.Add(new ImportedFolder(
                    Id: folderId,
                    CollectionId: collectionId,
                    ParentFolderId: parentFolderId,
                    Name: itemName,
                    Description: description,
                    RequestIds: folderRequestIds
                ));
            }
            else if (item.TryGetProperty("request", out var reqElem))
            {
                // It is a Request
                HttpRequestDefinition reqDef = ParseRequest(item, reqElem, collectionId, parentFolderId, itemName, description, unsupportedFeatures, compatibilityService);
                requests.Add(reqDef);
            }
        }
    }

    private static HttpRequestDefinition ParseRequest(
        JsonElement item,
        JsonElement reqElem,
        Guid collectionId,
        Guid? folderId,
        string itemName,
        string? description,
        List<string> unsupportedFeatures,
        PostmanCompatibilityService compatibilityService)
    {
        string method = reqElem.TryGetProperty("method", out var m) ? (m.GetString() ?? "GET").ToUpperInvariant() : "GET";
        string url = "http://localhost:5057/echo";
        var queryParams = new List<QueryParamEntry>();

        if (reqElem.TryGetProperty("url", out var urlElem))
        {
            if (urlElem.ValueKind == JsonValueKind.String)
            {
                url = urlElem.GetString() ?? url;
            }
            else if (urlElem.ValueKind == JsonValueKind.Object)
            {
                if (urlElem.TryGetProperty("raw", out var rawUrl) && rawUrl.GetString() is { } rawStr)
                {
                    url = rawStr;
                }

                if (urlElem.TryGetProperty("query", out var queryArray) && queryArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var q in queryArray.EnumerateArray())
                    {
                        string qKey = q.TryGetProperty("key", out var k) ? (k.GetString() ?? "") : "";
                        string qVal = q.TryGetProperty("value", out var v) ? (v.GetString() ?? "") : "";
                        bool qEnabled = !q.TryGetProperty("disabled", out var dis) || !dis.GetBoolean();
                        queryParams.Add(new QueryParamEntry(qKey, qVal, qEnabled));
                    }
                }
            }
        }

        // Headers
        var headers = new List<HeaderEntry>();
        if (reqElem.TryGetProperty("header", out var headerElem) && headerElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in headerElem.EnumerateArray())
            {
                string hKey = h.TryGetProperty("key", out var k) ? (k.GetString() ?? "") : "";
                string hVal = h.TryGetProperty("value", out var v) ? (v.GetString() ?? "") : "";
                bool hEnabled = !h.TryGetProperty("disabled", out var dis) || !dis.GetBoolean();
                if (!string.IsNullOrEmpty(hKey))
                {
                    headers.Add(new HeaderEntry(hKey, hVal, hEnabled));
                }
            }
        }

        // Body
        RequestBodyDefinition? bodyDef = null;
        if (reqElem.TryGetProperty("body", out var bodyElem) && bodyElem.ValueKind == JsonValueKind.Object)
        {
            string mode = bodyElem.TryGetProperty("mode", out var modeElem) ? (modeElem.GetString() ?? "raw") : "raw";
            if (mode == "raw")
            {
                string text = bodyElem.TryGetProperty("raw", out var rawText) ? (rawText.GetString() ?? "") : "";
                bodyDef = new RequestBodyDefinition { Mode = "raw", Text = text };
            }
            else if (mode == "urlencoded" && bodyElem.TryGetProperty("urlencoded", out var urlencElem) && urlencElem.ValueKind == JsonValueKind.Array)
            {
                var pairs = new List<string>();
                foreach (var param in urlencElem.EnumerateArray())
                {
                    bool enabled = !param.TryGetProperty("disabled", out var dis) || !dis.GetBoolean();
                    if (!enabled) continue;
                    string k = param.TryGetProperty("key", out var pk) ? (pk.GetString() ?? "") : "";
                    string v = param.TryGetProperty("value", out var pv) ? (pv.GetString() ?? "") : "";
                    pairs.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
                }
                bodyDef = new RequestBodyDefinition { Mode = "raw", Text = string.Join("&", pairs) };
            }
            else if (mode == "formdata" && bodyElem.TryGetProperty("formdata", out var formElem) && formElem.ValueKind == JsonValueKind.Array)
            {
                var formEntries = new List<FormDataEntry>();
                foreach (var param in formElem.EnumerateArray())
                {
                    bool enabled = !param.TryGetProperty("disabled", out var dis) || !dis.GetBoolean();
                    string k = param.TryGetProperty("key", out var pk) ? (pk.GetString() ?? "") : "";
                    string v = param.TryGetProperty("value", out var pv) ? (pv.GetString() ?? "") : "";
                    string? type = param.TryGetProperty("type", out var pt) ? pt.GetString() : "text";
                    formEntries.Add(new FormDataEntry(k, v, type == "file" ? v : null, null, enabled));
                }
                bodyDef = new RequestBodyDefinition { Mode = "formdata", FormData = formEntries };
            }
            else
            {
                unsupportedFeatures.Add($"Postman request body mode '{mode}' on request '{itemName}'.");
            }
        }

        // Auth
        var authDef = new RequestAuthDefinition();
        if (reqElem.TryGetProperty("auth", out var authElem) && authElem.ValueKind == JsonValueKind.Object)
        {
            string authType = authElem.TryGetProperty("type", out var at) ? (at.GetString() ?? "none") : "none";
            if (authType == "basic" && authElem.TryGetProperty("basic", out var basicArr) && basicArr.ValueKind == JsonValueKind.Array)
            {
                string u = "", p = "";
                foreach (var attr in basicArr.EnumerateArray())
                {
                    string k = attr.TryGetProperty("key", out var ak) ? (ak.GetString() ?? "") : "";
                    string v = attr.TryGetProperty("value", out var av) ? (av.GetString() ?? "") : "";
                    if (k == "username") u = v;
                    if (k == "password") p = v;
                }
                authDef = new RequestAuthDefinition { Type = "basic", Username = u, Password = p };
            }
            else if (authType == "bearer" && authElem.TryGetProperty("bearer", out var bearerArr) && bearerArr.ValueKind == JsonValueKind.Array)
            {
                string tok = "";
                foreach (var attr in bearerArr.EnumerateArray())
                {
                    string k = attr.TryGetProperty("key", out var ak) ? (ak.GetString() ?? "") : "";
                    string v = attr.TryGetProperty("value", out var av) ? (av.GetString() ?? "") : "";
                    if (k == "token") tok = v;
                }
                authDef = new RequestAuthDefinition { Type = "bearer", Token = tok };
            }
            else if (authType == "apikey" && authElem.TryGetProperty("apikey", out var apiArr) && apiArr.ValueKind == JsonValueKind.Array)
            {
                string key = "", val = "", where = "header";
                foreach (var attr in apiArr.EnumerateArray())
                {
                    string k = attr.TryGetProperty("key", out var ak) ? (ak.GetString() ?? "") : "";
                    string v = attr.TryGetProperty("value", out var av) ? (av.GetString() ?? "") : "";
                    if (k == "key") key = v;
                    if (k == "value") val = v;
                    if (k == "in") where = v;
                }
                authDef = new RequestAuthDefinition { Type = "apiKey", ApiKeyName = key, ApiKeyValue = val, ApiKeyLocation = where };
            }
            else if (authType != "noauth" && authType != "none")
            {
                unsupportedFeatures.Add($"Postman auth type '{authType}' on request '{itemName}'.");
            }
        }

        // Scripts in "event"
        string? preRequestScript = null;
        string? testScript = null;

        if (item.TryGetProperty("event", out var eventElem) && eventElem.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in eventElem.EnumerateArray())
            {
                string listen = ev.TryGetProperty("listen", out var l) ? (l.GetString() ?? "") : "";
                if (ev.TryGetProperty("script", out var scriptElem) && scriptElem.TryGetProperty("exec", out var execElem))
                {
                    string scriptCode = "";
                    if (execElem.ValueKind == JsonValueKind.Array)
                    {
                        var lines = new List<string>();
                        foreach (var line in execElem.EnumerateArray())
                        {
                            lines.Add(line.GetString() ?? "");
                        }
                        scriptCode = string.Join("\n", lines);
                    }
                    else if (execElem.ValueKind == JsonValueKind.String)
                    {
                        scriptCode = execElem.GetString() ?? "";
                    }

                    if (!string.IsNullOrWhiteSpace(scriptCode))
                    {
                        var analysis = compatibilityService.AnalyzeScript(scriptCode);
                        foreach (var unsupp in analysis.UnsupportedFeatures)
                        {
                            unsupportedFeatures.Add($"{unsupp} (in request '{itemName}')");
                        }

                        if (listen == "prerequest") preRequestScript = scriptCode;
                        else if (listen == "test") testScript = scriptCode;
                    }
                }
            }
        }

        return new HttpRequestDefinition
        {
            Id = Guid.NewGuid(),
            CollectionId = collectionId,
            FolderId = folderId,
            Name = itemName,
            Description = description,
            Method = method,
            Url = url,
            Headers = headers,
            Query = queryParams,
            BodyDefinition = bodyDef,
            Auth = authDef,
            PreRequestScript = preRequestScript,
            TestScript = testScript
        };
    }
}

