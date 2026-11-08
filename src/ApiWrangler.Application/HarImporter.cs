using System.Text.Json;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed record HarImportResult(
    string CollectionName,
    Guid CollectionId,
    IReadOnlyList<HttpRequestDefinition> Requests,
    IReadOnlyList<ImportedFolder> Folders,
    IReadOnlyList<string> UnsupportedFeatures,
    ImportedEntityCounts Counts
);

public static class HarImporter
{
    public static HarImportResult Import(string jsonContent, string? customCollectionName = null)
    {
        using var doc = JsonDocument.Parse(jsonContent);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("log", out var logElem) || logElem.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Invalid HAR archive: root object does not contain a 'log' property.");
        }

        string collectionName = customCollectionName ?? "HAR Import";
        if (customCollectionName == null && logElem.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
        {
            var firstPage = pages.EnumerateArray().FirstOrDefault();
            if (firstPage.ValueKind == JsonValueKind.Object && firstPage.TryGetProperty("title", out var title) && title.GetString() is { } t && !string.IsNullOrWhiteSpace(t))
            {
                collectionName = $"HAR: {t}";
            }
        }

        Guid collectionId = Guid.NewGuid();
        var unsupportedFeatures = new List<string>();
        var requests = new List<HttpRequestDefinition>();

        if (logElem.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
        {
            int index = 1;
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("request", out var reqElem) || reqElem.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string method = reqElem.TryGetProperty("method", out var m) ? (m.GetString() ?? "GET").ToUpperInvariant() : "GET";
                string url = reqElem.TryGetProperty("url", out var u) ? (u.GetString() ?? "") : "";

                if (string.IsNullOrWhiteSpace(url)) continue;

                // Headers
                var headers = new List<HeaderEntry>();
                if (reqElem.TryGetProperty("headers", out var headersElem) && headersElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var h in headersElem.EnumerateArray())
                    {
                        string hName = h.TryGetProperty("name", out var hn) ? (hn.GetString() ?? "") : "";
                        string hVal = h.TryGetProperty("value", out var hv) ? (hv.GetString() ?? "") : "";
                        if (!string.IsNullOrEmpty(hName))
                        {
                            headers.Add(new HeaderEntry(hName, hVal));
                        }
                    }
                }

                // Query Parameters
                var queryParams = new List<QueryParamEntry>();
                if (reqElem.TryGetProperty("queryString", out var queryElem) && queryElem.ValueKind == JsonValueKind.Array)
                {
                    foreach (var q in queryElem.EnumerateArray())
                    {
                        string qName = q.TryGetProperty("name", out var qn) ? (qn.GetString() ?? "") : "";
                        string qVal = q.TryGetProperty("value", out var qv) ? (qv.GetString() ?? "") : "";
                        if (!string.IsNullOrEmpty(qName))
                        {
                            queryParams.Add(new QueryParamEntry(qName, qVal));
                        }
                    }
                }

                // Post Data / Body
                RequestBodyDefinition? bodyDef = null;
                if (reqElem.TryGetProperty("postData", out var postElem) && postElem.ValueKind == JsonValueKind.Object)
                {
                    string mime = postElem.TryGetProperty("mimeType", out var mt) ? (mt.GetString() ?? "") : "";
                    string text = postElem.TryGetProperty("text", out var pt) ? (pt.GetString() ?? "") : "";

                    if (postElem.TryGetProperty("params", out var paramsElem) && paramsElem.ValueKind == JsonValueKind.Array && paramsElem.GetArrayLength() > 0)
                    {
                        var formData = new List<FormDataEntry>();
                        foreach (var p in paramsElem.EnumerateArray())
                        {
                            string pName = p.TryGetProperty("name", out var pn) ? (pn.GetString() ?? "") : "";
                            string pVal = p.TryGetProperty("value", out var pv) ? (pv.GetString() ?? "") : "";
                            string? file = p.TryGetProperty("fileName", out var fn) ? fn.GetString() : null;
                            string? cType = p.TryGetProperty("contentType", out var ct) ? ct.GetString() : null;
                            formData.Add(new FormDataEntry(pName, pVal, file, cType));
                        }
                        bodyDef = new RequestBodyDefinition { Mode = "formdata", FormData = formData };
                    }
                    else if (!string.IsNullOrEmpty(text))
                    {
                        bodyDef = new RequestBodyDefinition { Mode = "raw", Text = text };
                    }
                }

                string reqName = $"{method} {index}";
                try
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out var parsedUri))
                    {
                        string path = parsedUri.AbsolutePath.Trim('/');
                        reqName = string.IsNullOrEmpty(path) ? parsedUri.Host : $"{method} /{path}";
                    }
                }
                catch
                {
                    // fallback
                }

                requests.Add(new HttpRequestDefinition
                {
                    Id = Guid.NewGuid(),
                    CollectionId = collectionId,
                    FolderId = null,
                    Name = reqName,
                    Method = method,
                    Url = url,
                    Headers = headers,
                    Query = queryParams,
                    BodyDefinition = bodyDef
                });

                index++;
            }
        }

        var counts = new ImportedEntityCounts(
            Requests: requests.Count,
            Collections: 1,
            Folders: 0,
            Environments: 0,
            Variables: 0
        );

        return new HarImportResult(
            CollectionName: collectionName,
            CollectionId: collectionId,
            Requests: requests,
            Folders: Array.Empty<ImportedFolder>(),
            UnsupportedFeatures: unsupportedFeatures,
            Counts: counts
        );
    }
}

