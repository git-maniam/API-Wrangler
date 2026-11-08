using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public enum ImportSourceFormat
{
    AutoDetect,
    PostmanCollection,
    PostmanEnvironment,
    OpenApi,
    Curl,
    Har
}

public sealed record ImportAssetPreview(
    string Path,
    AssetKind Kind,
    string Name,
    JsonObject Content
);

public sealed record ImportPreview(
    ImportSourceFormat DetectedFormat,
    ImportedEntityCounts Counts,
    IReadOnlyList<SecretExtractionGuidance> DetectedSecrets,
    IReadOnlyList<string> UnsupportedFeatures,
    IReadOnlyList<ImportAssetPreview> ProposedAssets,
    IReadOnlyList<string> ValidationErrors
)
{
    public bool IsValid => ValidationErrors.Count == 0;
}

public sealed record ImportReport(
    string Id,
    DateTimeOffset TimestampUtc,
    ImportSourceFormat Format,
    ImportedEntityCounts Counts,
    IReadOnlyList<string> WrittenAssetPaths,
    IReadOnlyList<SecretExtractionGuidance> DetectedSecrets,
    IReadOnlyList<string> UnsupportedFeatures,
    string? ReportFilePath
);

public static class ProjectImportService
{
    public static ImportPreview Preview(string content, ImportSourceFormat format = ImportSourceFormat.AutoDetect)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return new ImportPreview(
                DetectedFormat: format,
                Counts: new ImportedEntityCounts(0, 0, 0, 0, 0),
                DetectedSecrets: Array.Empty<SecretExtractionGuidance>(),
                UnsupportedFeatures: Array.Empty<string>(),
                ProposedAssets: Array.Empty<ImportAssetPreview>(),
                ValidationErrors: ["Import source content cannot be empty."]
            );
        }

        ImportSourceFormat detectedFormat = format == ImportSourceFormat.AutoDetect
            ? DetectFormat(content)
            : format;

        try
        {
            return detectedFormat switch
            {
                ImportSourceFormat.PostmanCollection => PreviewPostmanCollection(content),
                ImportSourceFormat.PostmanEnvironment => PreviewPostmanEnvironment(content),
                ImportSourceFormat.OpenApi => PreviewOpenApi(content),
                ImportSourceFormat.Curl => PreviewCurl(content),
                ImportSourceFormat.Har => PreviewHar(content),
                _ => new ImportPreview(
                    DetectedFormat: detectedFormat,
                    Counts: new ImportedEntityCounts(0, 0, 0, 0, 0),
                    DetectedSecrets: Array.Empty<SecretExtractionGuidance>(),
                    UnsupportedFeatures: Array.Empty<string>(),
                    ProposedAssets: Array.Empty<ImportAssetPreview>(),
                    ValidationErrors: ["Unsupported or unrecognized import format."]
                )
            };
        }
        catch (Exception ex)
        {
            return new ImportPreview(
                DetectedFormat: detectedFormat,
                Counts: new ImportedEntityCounts(0, 0, 0, 0, 0),
                DetectedSecrets: Array.Empty<SecretExtractionGuidance>(),
                UnsupportedFeatures: Array.Empty<string>(),
                ProposedAssets: Array.Empty<ImportAssetPreview>(),
                ValidationErrors: [$"Failed to parse import source: {ex.Message}"]
            );
        }
    }

    public static async Task<ImportReport> PublishAsync(
        IProjectSession session,
        ImportPreview preview,
        CancellationToken token = default)
    {
        if (!preview.IsValid)
        {
            throw new InvalidOperationException($"Cannot publish invalid import: {string.Join("; ", preview.ValidationErrors)}");
        }

        // Convert proposed assets into AssetEdits
        var edits = preview.ProposedAssets.Select(asset => new AssetEdit(
            Path: asset.Path,
            Content: asset.Content,
            ExpectedHash: null // New file
        )).ToList();

        // Atomic publish using the project session transaction
        await session.SaveAsync(edits, token).ConfigureAwait(false);

        // Generate and write import report
        string reportId = Guid.NewGuid().ToString("N");
        var report = new ImportReport(
            Id: reportId,
            TimestampUtc: DateTimeOffset.UtcNow,
            Format: preview.DetectedFormat,
            Counts: preview.Counts,
            WrittenAssetPaths: preview.ProposedAssets.Select(a => a.Path).ToList(),
            DetectedSecrets: preview.DetectedSecrets,
            UnsupportedFeatures: preview.UnsupportedFeatures,
            ReportFilePath: $"reports/import-report-{reportId}.json"
        );

        // Save report to reports directory
        try
        {
            string reportsDir = Path.Combine(session.Root, "reports");
            Directory.CreateDirectory(reportsDir);
            string reportFullPath = Path.Combine(reportsDir, $"import-report-{reportId}.json");
            string reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(reportFullPath, reportJson, token).ConfigureAwait(false);
        }
        catch
        {
            // Non-fatal if report directory logging fails
        }

        return report;
    }

    private static ImportSourceFormat DetectFormat(string content)
    {
        string trimmed = content.Trim();
        if (trimmed.StartsWith("curl", StringComparison.OrdinalIgnoreCase))
        {
            return ImportSourceFormat.Curl;
        }

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                if (root.TryGetProperty("info", out var info) && info.TryGetProperty("schema", out var s) &&
                    s.GetString()?.Contains("getpostman.com", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return ImportSourceFormat.PostmanCollection;
                }

                if (root.TryGetProperty("_postman_variable_scope", out _) ||
                    (root.TryGetProperty("values", out _) && root.TryGetProperty("name", out _)))
                {
                    return ImportSourceFormat.PostmanEnvironment;
                }

                if (root.TryGetProperty("openapi", out _) || root.TryGetProperty("swagger", out _))
                {
                    return ImportSourceFormat.OpenApi;
                }

                if (root.TryGetProperty("log", out var log) && log.TryGetProperty("version", out _) && log.TryGetProperty("entries", out _))
                {
                    return ImportSourceFormat.Har;
                }

                if (root.TryGetProperty("item", out _))
                {
                    return ImportSourceFormat.PostmanCollection;
                }
            }
            catch
            {
                // Fall back to Curl or Unknown
            }
        }

        return ImportSourceFormat.Curl;
    }

    private static ImportPreview PreviewPostmanCollection(string content)
    {
        var result = PostmanImporter.ImportCollection(content);
        var proposedAssets = new List<ImportAssetPreview>();
        string colSlug = ToSlug(result.CollectionName);

        // 1. Collection asset
        var colObj = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = result.CollectionId.ToString(),
            ["name"] = result.CollectionName,
            ["requestIds"] = new JsonArray(result.Requests.Select(r => (JsonNode)JsonValue.Create(r.Id.ToString())).ToArray())
        };

        if (result.Folders.Count > 0)
        {
            colObj["folderIds"] = new JsonArray(result.Folders.Select(f => (JsonNode)JsonValue.Create(f.Id.ToString())).ToArray());
        }

        if (result.Variables.Count > 0)
        {
            var varObj = new JsonObject();
            foreach (var v in result.Variables)
            {
                varObj[v.Name] = v.Value;
            }
            colObj["variables"] = varObj;
        }

        proposedAssets.Add(new ImportAssetPreview(
            Path: $"collections/{colSlug}/collection.json",
            Kind: AssetKind.Collection,
            Name: result.CollectionName,
            Content: colObj
        ));

        // 2. Folder assets
        var usedFolderSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in result.Folders)
        {
            string fSlug = UniqueSlug(folder.Name, usedFolderSlugs);
            var folderObj = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["id"] = folder.Id.ToString(),
                ["collectionId"] = folder.CollectionId.ToString(),
                ["name"] = folder.Name
            };
            if (folder.ParentFolderId.HasValue && folder.ParentFolderId.Value != Guid.Empty)
            {
                folderObj["parentFolderId"] = folder.ParentFolderId.Value.ToString();
            }
            if (folder.RequestIds.Count > 0)
            {
                folderObj["requestIds"] = new JsonArray(folder.RequestIds.Select(id => (JsonNode)JsonValue.Create(id.ToString())).ToArray());
            }

            proposedAssets.Add(new ImportAssetPreview(
                Path: $"collections/{colSlug}/{fSlug}.folder.json",
                Kind: AssetKind.Folder,
                Name: folder.Name,
                Content: folderObj
            ));
        }

        // 3. Request assets
        var usedRequestSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in result.Requests)
        {
            string rSlug = UniqueSlug(req.Name, usedRequestSlugs);
            JsonObject reqObj = RequestAssetSerializer.ToJsonObject(req);

            proposedAssets.Add(new ImportAssetPreview(
                Path: $"collections/{colSlug}/{rSlug}.request.json",
                Kind: AssetKind.Request,
                Name: req.Name,
                Content: reqObj
            ));
        }

        return new ImportPreview(
            DetectedFormat: ImportSourceFormat.PostmanCollection,
            Counts: result.Counts,
            DetectedSecrets: result.DetectedSecrets,
            UnsupportedFeatures: result.UnsupportedFeatures,
            ProposedAssets: proposedAssets,
            ValidationErrors: Array.Empty<string>()
        );
    }

    private static ImportPreview PreviewPostmanEnvironment(string content)
    {
        var result = PostmanImporter.ImportEnvironment(content);
        string envSlug = ToSlug(result.EnvironmentName);

        var varObj = new JsonObject();
        var secObj = new JsonObject();

        foreach (var v in result.Variables)
        {
            if (v.IsSecret)
            {
                secObj[v.Name] = new JsonObject { ["reference"] = $"secret:{v.Name}" };
            }
            else
            {
                varObj[v.Name] = v.Value;
            }
        }

        var envObj = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = result.EnvironmentId.ToString(),
            ["name"] = result.EnvironmentName,
            ["variables"] = varObj,
            ["secrets"] = secObj
        };

        var proposedAssets = new List<ImportAssetPreview>
        {
            new(
                Path: $"environments/{envSlug}.environment.json",
                Kind: AssetKind.Environment,
                Name: result.EnvironmentName,
                Content: envObj
            )
        };

        var counts = new ImportedEntityCounts(
            Requests: 0,
            Collections: 0,
            Folders: 0,
            Environments: 1,
            Variables: result.VariableCount
        );

        return new ImportPreview(
            DetectedFormat: ImportSourceFormat.PostmanEnvironment,
            Counts: counts,
            DetectedSecrets: result.DetectedSecrets,
            UnsupportedFeatures: Array.Empty<string>(),
            ProposedAssets: proposedAssets,
            ValidationErrors: Array.Empty<string>()
        );
    }

    private static ImportPreview PreviewOpenApi(string content)
    {
        var result = OpenApiImporter.Import(content);
        var proposedAssets = new List<ImportAssetPreview>();
        string colSlug = ToSlug(result.CollectionName);

        // 1. Collection asset
        var colObj = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = result.CollectionId.ToString(),
            ["name"] = result.CollectionName,
            ["requestIds"] = new JsonArray(result.Requests.Select(r => (JsonNode)JsonValue.Create(r.Id.ToString())).ToArray())
        };

        if (result.Folders.Count > 0)
        {
            colObj["folderIds"] = new JsonArray(result.Folders.Select(f => (JsonNode)JsonValue.Create(f.Id.ToString())).ToArray());
        }

        if (result.Variables.Count > 0)
        {
            var varObj = new JsonObject();
            foreach (var v in result.Variables)
            {
                varObj[v.Name] = v.Value;
            }
            colObj["variables"] = varObj;
        }

        proposedAssets.Add(new ImportAssetPreview(
            Path: $"collections/{colSlug}/collection.json",
            Kind: AssetKind.Collection,
            Name: result.CollectionName,
            Content: colObj
        ));

        // 2. Folders
        var usedFolderSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in result.Folders)
        {
            string fSlug = UniqueSlug(folder.Name, usedFolderSlugs);
            var folderObj = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["id"] = folder.Id.ToString(),
                ["collectionId"] = folder.CollectionId.ToString(),
                ["name"] = folder.Name
            };
            if (folder.RequestIds.Count > 0)
            {
                folderObj["requestIds"] = new JsonArray(folder.RequestIds.Select(id => (JsonNode)JsonValue.Create(id.ToString())).ToArray());
            }

            proposedAssets.Add(new ImportAssetPreview(
                Path: $"collections/{colSlug}/{fSlug}.folder.json",
                Kind: AssetKind.Folder,
                Name: folder.Name,
                Content: folderObj
            ));
        }

        // 3. Requests
        var usedRequestSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in result.Requests)
        {
            string rSlug = UniqueSlug(req.Name, usedRequestSlugs);
            JsonObject reqObj = RequestAssetSerializer.ToJsonObject(req);

            proposedAssets.Add(new ImportAssetPreview(
                Path: $"collections/{colSlug}/{rSlug}.request.json",
                Kind: AssetKind.Request,
                Name: req.Name,
                Content: reqObj
            ));
        }

        return new ImportPreview(
            DetectedFormat: ImportSourceFormat.OpenApi,
            Counts: result.Counts,
            DetectedSecrets: Array.Empty<SecretExtractionGuidance>(),
            UnsupportedFeatures: result.UnsupportedFeatures,
            ProposedAssets: proposedAssets,
            ValidationErrors: Array.Empty<string>()
        );
    }

    private static ImportPreview PreviewCurl(string command)
    {
        HttpRequestDefinition req = CurlParser.Parse(command);
        Guid collectionId = Guid.NewGuid();
        string colSlug = "curl-imports";

        var reqWithCol = req with { CollectionId = collectionId };
        var colObj = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = collectionId.ToString(),
            ["name"] = "cURL Imports",
            ["requestIds"] = new JsonArray((JsonNode)JsonValue.Create(req.Id.ToString()))
        };

        string rSlug = ToSlug(req.Name);
        var reqObj = RequestAssetSerializer.ToJsonObject(reqWithCol);

        var proposedAssets = new List<ImportAssetPreview>
        {
            new(
                Path: $"collections/{colSlug}/collection.json",
                Kind: AssetKind.Collection,
                Name: "cURL Imports",
                Content: colObj
            ),
            new(
                Path: $"collections/{colSlug}/{rSlug}.request.json",
                Kind: AssetKind.Request,
                Name: req.Name,
                Content: reqObj
            )
        };

        var counts = new ImportedEntityCounts(
            Requests: 1,
            Collections: 1,
            Folders: 0,
            Environments: 0,
            Variables: 0
        );

        return new ImportPreview(
            DetectedFormat: ImportSourceFormat.Curl,
            Counts: counts,
            DetectedSecrets: Array.Empty<SecretExtractionGuidance>(),
            UnsupportedFeatures: Array.Empty<string>(),
            ProposedAssets: proposedAssets,
            ValidationErrors: Array.Empty<string>()
        );
    }

    private static ImportPreview PreviewHar(string content)
    {
        var result = HarImporter.Import(content);
        var proposedAssets = new List<ImportAssetPreview>();
        string colSlug = ToSlug(result.CollectionName);

        var colObj = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = result.CollectionId.ToString(),
            ["name"] = result.CollectionName,
            ["requestIds"] = new JsonArray(result.Requests.Select(r => (JsonNode)JsonValue.Create(r.Id.ToString())).ToArray())
        };

        proposedAssets.Add(new ImportAssetPreview(
            Path: $"collections/{colSlug}/collection.json",
            Kind: AssetKind.Collection,
            Name: result.CollectionName,
            Content: colObj
        ));

        var usedRequestSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var req in result.Requests)
        {
            string rSlug = UniqueSlug(req.Name, usedRequestSlugs);
            JsonObject reqObj = RequestAssetSerializer.ToJsonObject(req);

            proposedAssets.Add(new ImportAssetPreview(
                Path: $"collections/{colSlug}/{rSlug}.request.json",
                Kind: AssetKind.Request,
                Name: req.Name,
                Content: reqObj
            ));
        }

        return new ImportPreview(
            DetectedFormat: ImportSourceFormat.Har,
            Counts: result.Counts,
            DetectedSecrets: Array.Empty<SecretExtractionGuidance>(),
            UnsupportedFeatures: result.UnsupportedFeatures,
            ProposedAssets: proposedAssets,
            ValidationErrors: Array.Empty<string>()
        );
    }

    private static string ToSlug(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "untitled";
        var chars = name.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        string slug = new string(chars);
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? "untitled" : slug;
    }

    private static string UniqueSlug(string name, HashSet<string> existing)
    {
        string baseSlug = ToSlug(name);
        string candidate = baseSlug;
        int counter = 1;
        while (!existing.Add(candidate))
        {
            candidate = $"{baseSlug}-{counter++}";
        }
        return candidate;
    }
}
