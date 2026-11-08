using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed record ExportReport(
    string Format,
    int ExportedRequests,
    int ExportedFolders,
    int ExportedVariables,
    IReadOnlyList<string> RedactedSecrets,
    IReadOnlyList<string> LossyConversions
);

public static class PostmanExporter
{
    public static (string Json, ExportReport Report) ExportCollection(
        string collectionName,
        Guid collectionId,
        IReadOnlyList<ImportedFolder> folders,
        IReadOnlyList<HttpRequestDefinition> requests,
        IReadOnlyList<VariableEntry>? variables = null)
    {
        var lossyConversions = new List<string>();

        var root = new JsonObject
        {
            ["info"] = new JsonObject
            {
                ["_postman_id"] = collectionId.ToString(),
                ["name"] = collectionName,
                ["schema"] = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
            }
        };

        if (variables is { Count: > 0 })
        {
            var varArray = new JsonArray();
            foreach (var v in variables)
            {
                varArray.Add(new JsonObject
                {
                    ["key"] = v.Name,
                    ["value"] = v.Value,
                    ["type"] = v.IsSecret ? "secret" : "string"
                });
            }
            root["variable"] = varArray;
        }

        var itemsArray = new JsonArray();

        // 1. Root requests (requests with no FolderId)
        var rootRequests = requests.Where(r => r.FolderId is null || r.FolderId == Guid.Empty).ToList();
        foreach (var req in rootRequests)
        {
            itemsArray.Add(SerializeRequestItem(req, lossyConversions));
        }

        // 2. Folders (top-level folders first, nested folders recursively)
        var topFolders = folders.Where(f => f.ParentFolderId is null || f.ParentFolderId == Guid.Empty).ToList();
        foreach (var folder in topFolders)
        {
            itemsArray.Add(SerializeFolder(folder, folders, requests, lossyConversions));
        }

        root["item"] = itemsArray;

        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = root.ToJsonString(options);

        var report = new ExportReport(
            Format: "Postman Collection v2.1",
            ExportedRequests: requests.Count,
            ExportedFolders: folders.Count,
            ExportedVariables: variables?.Count ?? 0,
            RedactedSecrets: Array.Empty<string>(),
            LossyConversions: lossyConversions
        );

        return (json, report);
    }

    public static (string Json, ExportReport Report) ExportEnvironment(
        string environmentName,
        Guid environmentId,
        IReadOnlyList<VariableEntry> variables,
        bool redactSecrets = true)
    {
        var redactedSecrets = new List<string>();
        var valuesArray = new JsonArray();

        foreach (var v in variables)
        {
            string valueToExport = v.Value;
            if (v.IsSecret && redactSecrets)
            {
                valueToExport = "";
                redactedSecrets.Add(v.Name);
            }

            valuesArray.Add(new JsonObject
            {
                ["key"] = v.Name,
                ["value"] = valueToExport,
                ["enabled"] = true,
                ["type"] = v.IsSecret ? "secret" : "default"
            });
        }

        var root = new JsonObject
        {
            ["id"] = environmentId.ToString(),
            ["name"] = environmentName,
            ["values"] = valuesArray,
            ["_postman_variable_scope"] = "environment"
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = root.ToJsonString(options);

        var report = new ExportReport(
            Format: "Postman Environment v2.1",
            ExportedRequests: 0,
            ExportedFolders: 0,
            ExportedVariables: variables.Count,
            RedactedSecrets: redactedSecrets,
            LossyConversions: Array.Empty<string>()
        );

        return (json, report);
    }

    private static JsonObject SerializeFolder(
        ImportedFolder folder,
        IReadOnlyList<ImportedFolder> allFolders,
        IReadOnlyList<HttpRequestDefinition> allRequests,
        List<string> lossyConversions)
    {
        var folderObj = new JsonObject
        {
            ["name"] = folder.Name
        };

        if (!string.IsNullOrEmpty(folder.Description))
        {
            folderObj["description"] = folder.Description;
        }

        var folderItems = new JsonArray();

        // Requests in this folder
        var folderRequests = allRequests.Where(r => r.FolderId == folder.Id).ToList();
        foreach (var req in folderRequests)
        {
            folderItems.Add(SerializeRequestItem(req, lossyConversions));
        }

        // Child folders
        var childFolders = allFolders.Where(f => f.ParentFolderId == folder.Id).ToList();
        foreach (var child in childFolders)
        {
            folderItems.Add(SerializeFolder(child, allFolders, allRequests, lossyConversions));
        }

        folderObj["item"] = folderItems;
        return folderObj;
    }

    private static JsonObject SerializeRequestItem(HttpRequestDefinition req, List<string> lossyConversions)
    {
        var itemObj = new JsonObject
        {
            ["name"] = req.Name
        };

        var reqObj = new JsonObject
        {
            ["method"] = req.Method
        };

        // Headers
        var headerArr = new JsonArray();
        foreach (var h in req.Headers)
        {
            headerArr.Add(new JsonObject
            {
                ["key"] = h.Name,
                ["value"] = h.Value,
                ["disabled"] = !h.Enabled
            });
        }
        reqObj["header"] = headerArr;

        // URL
        var urlObj = new JsonObject
        {
            ["raw"] = req.Url
        };
        if (req.Query.Count > 0)
        {
            var queryArr = new JsonArray();
            foreach (var q in req.Query)
            {
                queryArr.Add(new JsonObject
                {
                    ["key"] = q.Name,
                    ["value"] = q.Value,
                    ["disabled"] = !q.Enabled
                });
            }
            urlObj["query"] = queryArr;
        }
        reqObj["url"] = urlObj;

        // Body
        if (req.BodyDefinition is not null)
        {
            if (req.BodyDefinition.Mode == "raw")
            {
                reqObj["body"] = new JsonObject
                {
                    ["mode"] = "raw",
                    ["raw"] = req.BodyDefinition.Text ?? ""
                };
            }
            else if (req.BodyDefinition.Mode == "formdata")
            {
                var formArr = new JsonArray();
                foreach (var f in req.BodyDefinition.FormData)
                {
                    formArr.Add(new JsonObject
                    {
                        ["key"] = f.Name,
                        ["value"] = f.Value,
                        ["type"] = f.FilePath != null ? "file" : "text",
                        ["disabled"] = !f.Enabled
                    });
                }
                reqObj["body"] = new JsonObject
                {
                    ["mode"] = "formdata",
                    ["formdata"] = formArr
                };
            }
            else
            {
                lossyConversions.Add($"Request '{req.Name}' body mode '{req.BodyDefinition.Mode}' converted to empty body.");
            }
        }

        // Auth
        if (!string.IsNullOrEmpty(req.Auth.Type) && req.Auth.Type != "none")
        {
            if (req.Auth.Type == "basic")
            {
                reqObj["auth"] = new JsonObject
                {
                    ["type"] = "basic",
                    ["basic"] = new JsonArray(
                        new JsonObject { ["key"] = "username", ["value"] = req.Auth.Username ?? "", ["type"] = "string" },
                        new JsonObject { ["key"] = "password", ["value"] = req.Auth.Password ?? "", ["type"] = "string" }
                    )
                };
            }
            else if (req.Auth.Type == "bearer")
            {
                reqObj["auth"] = new JsonObject
                {
                    ["type"] = "bearer",
                    ["bearer"] = new JsonArray(
                        new JsonObject { ["key"] = "token", ["value"] = req.Auth.Token ?? "", ["type"] = "string" }
                    )
                };
            }
            else if (req.Auth.Type == "apiKey")
            {
                reqObj["auth"] = new JsonObject
                {
                    ["type"] = "apikey",
                    ["apikey"] = new JsonArray(
                        new JsonObject { ["key"] = "key", ["value"] = req.Auth.ApiKeyName ?? "", ["type"] = "string" },
                        new JsonObject { ["key"] = "value", ["value"] = req.Auth.ApiKeyValue ?? "", ["type"] = "string" },
                        new JsonObject { ["key"] = "in", ["value"] = req.Auth.ApiKeyLocation ?? "header", ["type"] = "string" }
                    )
                };
            }
            else
            {
                lossyConversions.Add($"Request '{req.Name}' auth type '{req.Auth.Type}' is not supported in Postman export.");
            }
        }

        itemObj["request"] = reqObj;

        // Events (pre-request and test scripts)
        var eventArr = new JsonArray();
        if (!string.IsNullOrEmpty(req.PreRequestScript))
        {
            eventArr.Add(new JsonObject
            {
                ["listen"] = "prerequest",
                ["script"] = new JsonObject
                {
                    ["type"] = "text/javascript",
                    ["exec"] = new JsonArray(req.PreRequestScript.Split('\n').Select(l => (JsonNode)JsonValue.Create(l.TrimEnd('\r'))).ToArray())
                }
            });
        }

        if (!string.IsNullOrEmpty(req.TestScript))
        {
            eventArr.Add(new JsonObject
            {
                ["listen"] = "test",
                ["script"] = new JsonObject
                {
                    ["type"] = "text/javascript",
                    ["exec"] = new JsonArray(req.TestScript.Split('\n').Select(l => (JsonNode)JsonValue.Create(l.TrimEnd('\r'))).ToArray())
                }
            });
        }

        if (eventArr.Count > 0)
        {
            itemObj["event"] = eventArr;
        }

        return itemObj;
    }
}

