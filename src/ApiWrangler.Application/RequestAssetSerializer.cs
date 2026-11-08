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

        var authObj = new JsonObject
        {
            ["type"] = string.IsNullOrWhiteSpace(request.Auth.Type) ? "none" : request.Auth.Type
        };
        if (!string.IsNullOrEmpty(request.Auth.Username)) authObj["username"] = request.Auth.Username;
        if (!string.IsNullOrEmpty(request.Auth.Password)) authObj["password"] = request.Auth.Password;
        if (!string.IsNullOrEmpty(request.Auth.Token)) authObj["token"] = request.Auth.Token;
        if (!string.IsNullOrEmpty(request.Auth.ApiKeyName)) authObj["apiKeyName"] = request.Auth.ApiKeyName;
        if (!string.IsNullOrEmpty(request.Auth.ApiKeyValue)) authObj["apiKeyValue"] = request.Auth.ApiKeyValue;
        if (!string.IsNullOrEmpty(request.Auth.ApiKeyLocation)) authObj["apiKeyLocation"] = request.Auth.ApiKeyLocation;

        if (!string.IsNullOrEmpty(request.Auth.OAuthGrantType)) authObj["oauthGrantType"] = request.Auth.OAuthGrantType;
        if (!string.IsNullOrEmpty(request.Auth.OAuthTokenUrl)) authObj["oauthTokenUrl"] = request.Auth.OAuthTokenUrl;
        if (!string.IsNullOrEmpty(request.Auth.OAuthClientId)) authObj["oauthClientId"] = request.Auth.OAuthClientId;
        if (!string.IsNullOrEmpty(request.Auth.OAuthClientSecret)) authObj["oauthClientSecret"] = request.Auth.OAuthClientSecret;
        if (!string.IsNullOrEmpty(request.Auth.OAuthScope)) authObj["oauthScope"] = request.Auth.OAuthScope;
        if (!string.IsNullOrEmpty(request.Auth.OAuthRefreshToken)) authObj["oauthRefreshToken"] = request.Auth.OAuthRefreshToken;
        if (!string.IsNullOrEmpty(request.Auth.OAuthAccessToken)) authObj["oauthAccessToken"] = request.Auth.OAuthAccessToken;
        if (request.Auth.OAuthExpiresAt.HasValue) authObj["oauthExpiresAt"] = request.Auth.OAuthExpiresAt.Value.ToString("o");
        if (request.Auth.OAuthUsePkce) authObj["oauthUsePkce"] = true;
        if (!string.IsNullOrEmpty(request.Auth.OAuthCode)) authObj["oauthCode"] = request.Auth.OAuthCode;
        if (!string.IsNullOrEmpty(request.Auth.OAuthRedirectUri)) authObj["oauthRedirectUri"] = request.Auth.OAuthRedirectUri;
        if (!string.IsNullOrEmpty(request.Auth.OAuthCodeVerifier)) authObj["oauthCodeVerifier"] = request.Auth.OAuthCodeVerifier;

        if (!request.Auth.UseDefaultWindowsCredentials) authObj["useDefaultWindowsCredentials"] = false;
        if (!string.IsNullOrEmpty(request.Auth.WindowsDomain)) authObj["windowsDomain"] = request.Auth.WindowsDomain;
        if (!string.IsNullOrEmpty(request.Auth.WindowsUsername)) authObj["windowsUsername"] = request.Auth.WindowsUsername;
        if (!string.IsNullOrEmpty(request.Auth.WindowsPassword)) authObj["windowsPassword"] = request.Auth.WindowsPassword;

        if (!string.IsNullOrEmpty(request.Auth.ClientCertificatePath)) authObj["clientCertificatePath"] = request.Auth.ClientCertificatePath;
        if (!string.IsNullOrEmpty(request.Auth.ClientCertificatePassword)) authObj["clientCertificatePassword"] = request.Auth.ClientCertificatePassword;
        obj["auth"] = authObj;

        if (request.BodyDefinition is not null)
        {
            obj["body"] = new JsonObject
            {
                ["mode"] = request.BodyDefinition.Mode,
                ["text"] = request.BodyDefinition.Text ?? ""
            };
        }

        if (request.Assertions.Count > 0)
        {
            var assertArr = new JsonArray();
            foreach (var a in request.Assertions)
            {
                var aObj = new JsonObject
                {
                    ["id"] = a.Id.ToString(),
                    ["type"] = a.Type,
                    ["operator"] = a.Operator
                };
                if (!string.IsNullOrEmpty(a.Name)) aObj["name"] = a.Name;
                if (!string.IsNullOrEmpty(a.Target)) aObj["target"] = a.Target;
                if (a.Expected is not null) aObj["expected"] = a.Expected.DeepClone();
                assertArr.Add(aObj);
            }
            obj["assertions"] = assertArr;
        }

        if (!string.IsNullOrEmpty(request.PreRequestScript))
            obj["preRequestScript"] = request.PreRequestScript;
        if (!string.IsNullOrEmpty(request.TestScript))
            obj["testScript"] = request.TestScript;

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

        RequestAuthDefinition authDef = new();
        if (json["auth"] is JsonObject aObj)
        {
            var type = aObj["type"]?.GetValue<string>() ?? "none";
            DateTimeOffset? expiresAt = null;
            if (DateTimeOffset.TryParse(aObj["oauthExpiresAt"]?.GetValue<string>(), out var dt))
                expiresAt = dt;

            authDef = new RequestAuthDefinition
            {
                Type = type,
                Username = aObj["username"]?.GetValue<string>(),
                Password = aObj["password"]?.GetValue<string>(),
                Token = aObj["token"]?.GetValue<string>(),
                ApiKeyName = aObj["apiKeyName"]?.GetValue<string>(),
                ApiKeyValue = aObj["apiKeyValue"]?.GetValue<string>(),
                ApiKeyLocation = aObj["apiKeyLocation"]?.GetValue<string>() ?? "header",
                OAuthGrantType = aObj["oauthGrantType"]?.GetValue<string>() ?? "client_credentials",
                OAuthTokenUrl = aObj["oauthTokenUrl"]?.GetValue<string>(),
                OAuthClientId = aObj["oauthClientId"]?.GetValue<string>(),
                OAuthClientSecret = aObj["oauthClientSecret"]?.GetValue<string>(),
                OAuthScope = aObj["oauthScope"]?.GetValue<string>(),
                OAuthRefreshToken = aObj["oauthRefreshToken"]?.GetValue<string>(),
                OAuthAccessToken = aObj["oauthAccessToken"]?.GetValue<string>(),
                OAuthExpiresAt = expiresAt,
                OAuthUsePkce = aObj["oauthUsePkce"] is JsonValue pkVal && pkVal.TryGetValue<bool>(out var pk) && pk,
                OAuthCode = aObj["oauthCode"]?.GetValue<string>(),
                OAuthRedirectUri = aObj["oauthRedirectUri"]?.GetValue<string>(),
                OAuthCodeVerifier = aObj["oauthCodeVerifier"]?.GetValue<string>(),
                UseDefaultWindowsCredentials = !(aObj["useDefaultWindowsCredentials"] is JsonValue wVal && wVal.TryGetValue<bool>(out var wb) && !wb),
                WindowsDomain = aObj["windowsDomain"]?.GetValue<string>(),
                WindowsUsername = aObj["windowsUsername"]?.GetValue<string>(),
                WindowsPassword = aObj["windowsPassword"]?.GetValue<string>(),
                ClientCertificatePath = aObj["clientCertificatePath"]?.GetValue<string>(),
                ClientCertificatePassword = aObj["clientCertificatePassword"]?.GetValue<string>()
            };
        }

        var assertions = new List<RequestAssertionDefinition>();
        if (json["assertions"] is JsonArray assertArr)
        {
            foreach (var node in assertArr)
            {
                if (node is JsonObject asObj)
                {
                    var aId = Guid.TryParse(asObj["id"]?.GetValue<string>(), out var pAId) ? pAId : Guid.NewGuid();
                    var aType = asObj["type"]?.GetValue<string>() ?? "status";
                    var aOp = asObj["operator"]?.GetValue<string>() ?? "equals";
                    var aName = asObj["name"]?.GetValue<string>();
                    var aTarget = asObj["target"]?.GetValue<string>();
                    var aExpected = asObj["expected"]?.DeepClone();

                    assertions.Add(new RequestAssertionDefinition
                    {
                        Id = aId,
                        Name = aName,
                        Type = aType,
                        Operator = aOp,
                        Target = aTarget,
                        Expected = aExpected
                    });
                }
            }
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
            BodyDefinition = bodyDef,
            Auth = authDef,
            Assertions = assertions,
            PreRequestScript = json["preRequestScript"]?.GetValue<string>(),
            TestScript = json["testScript"]?.GetValue<string>()
        };
    }
}

