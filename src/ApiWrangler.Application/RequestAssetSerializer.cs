using System.Text.Json.Nodes;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public static class RequestAssetSerializer
{
    public static JsonObject ToJsonObject(HttpRequestDefinition request)
    {
        var obj = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = request.Id.ToString(),
            ["name"] = string.IsNullOrWhiteSpace(request.Name) ? "Untitled request" : request.Name,
            ["protocol"] = "http",
            ["method"] = string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method.ToUpperInvariant(),
            ["url"] = string.IsNullOrWhiteSpace(request.Url) ? "http://localhost:5057/echo" : request.Url,
            ["timeoutMs"] = request.TimeoutMs > 0 ? request.TimeoutMs : 30_000
        };

        if (!string.IsNullOrEmpty(request.Description))
            obj["description"] = request.Description;
        if (request.CollectionId.HasValue && request.CollectionId.Value != Guid.Empty)
            obj["collectionId"] = request.CollectionId.Value.ToString();
        if (request.FolderId.HasValue && request.FolderId.Value != Guid.Empty)
            obj["folderId"] = request.FolderId.Value.ToString();

        var headersArray = new JsonArray();
        foreach (var header in request.Headers)
        {
            headersArray.Add(new JsonObject
            {
                ["name"] = header.Name,
                ["value"] = header.Value,
                ["enabled"] = header.Enabled
            });
        }
        obj["headers"] = headersArray;

        var queryArray = new JsonArray();
        foreach (var query in request.Query)
        {
            queryArray.Add(new JsonObject
            {
                ["name"] = query.Name,
                ["value"] = query.Value,
                ["enabled"] = query.Enabled
            });
        }
        obj["query"] = queryArray;

        obj["auth"] = new JsonObject { ["type"] = "none" };

        if (request.BodyDefinition is not null)
        {
            obj["body"] = new JsonObject
            {
                ["mode"] = request.BodyDefinition.Mode,
                ["text"] = request.BodyDefinition.Text ?? ""
            };
        }

        return obj;
    }

    public static HttpRequestDefinition FromJsonObject(JsonObject json)
    {
        var id = Guid.TryParse(json["id"]?.GetValue<string>(), out var parsedId) ? parsedId : Guid.NewGuid();
        var name = json["name"]?.GetValue<string>() ?? "Untitled request";
        var description = json["description"]?.GetValue<string>();
        var collectionId = Guid.TryParse(json["collectionId"]?.GetValue<string>(), out var cId) ? cId : (Guid?)null;
        var folderId = Guid.TryParse(json["folderId"]?.GetValue<string>(), out var fId) ? fId : (Guid?)null;
        var protocol = json["protocol"]?.GetValue<string>() ?? "http";
        var method = json["method"]?.GetValue<string>() ?? "GET";
        var url = json["url"]?.GetValue<string>() ?? "http://localhost:5057/echo";
        var timeoutMs = json["timeoutMs"] is JsonValue tVal && tVal.TryGetValue<int>(out var t) ? t : 30_000;

        var headers = new List<HeaderEntry>();
        if (json["headers"] is JsonArray headersArr)
        {
            foreach (var node in headersArr)
            {
                if (node is JsonObject hObj)
                {
                    var hName = hObj["name"]?.GetValue<string>() ?? "";
                    var hVal = hObj["value"]?.GetValue<string>() ?? "";
                    var enabled = hObj["enabled"] is JsonValue eVal && eVal.TryGetValue<bool>(out var b) ? b : true;
                    if (!string.IsNullOrEmpty(hName))
                        headers.Add(new HeaderEntry(hName, hVal, enabled));
                }
            }
        }

        var query = new List<QueryParamEntry>();
        if (json["query"] is JsonArray queryArr)
        {
            foreach (var node in queryArr)
            {
                if (node is JsonObject qObj)
                {
                    var qName = qObj["name"]?.GetValue<string>() ?? "";
                    var qVal = qObj["value"]?.GetValue<string>() ?? "";
                    var enabled = qObj["enabled"] is JsonValue eVal && eVal.TryGetValue<bool>(out var b) ? b : true;
                    if (!string.IsNullOrEmpty(qName))
                        query.Add(new QueryParamEntry(qName, qVal, enabled));
                }
            }
        }

        RequestBodyDefinition? bodyDef = null;
        if (json["body"] is JsonObject bodyObj)
        {
            var mode = bodyObj["mode"]?.GetValue<string>() ?? "raw";
            var text = bodyObj["text"]?.GetValue<string>();
            bodyDef = new RequestBodyDefinition
            {
                Mode = mode,
                Text = text
            };
        }

        return new HttpRequestDefinition
        {
            Id = id,
            Name = name,
            Description = description,
            CollectionId = collectionId,
            FolderId = folderId,
            Protocol = protocol,
            Method = method,
            Url = url,
            TimeoutMs = timeoutMs,
            Headers = headers,
            Query = query,
            BodyDefinition = bodyDef
        };
    }
}

