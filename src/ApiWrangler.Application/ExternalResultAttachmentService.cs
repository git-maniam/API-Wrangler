using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public static class ExternalResultAttachmentService
{
    public static ExternalResultAttachment CreateAttachment(
        string rawPayload,
        string? specifiedSourceTool = null,
        string? testMapping = null,
        string? environmentLabel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPayload);

        var tool = specifiedSourceTool ?? DetectSourceTool(rawPayload);
        var status = DetectStatus(rawPayload, tool);

        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(rawPayload));
        var contentHash = Convert.ToHexStringLower(hashBytes);

        return new ExternalResultAttachment(
            SourceTool: tool,
            ImportTimeUtc: DateTimeOffset.UtcNow,
            ContentHash: contentHash,
            TestMapping: testMapping,
            EnvironmentLabel: environmentLabel,
            ExternalStatus: status,
            RawPayload: rawPayload);
    }

    public static string SerializeAttachment(ExternalResultAttachment attachment)
    {
        var json = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["isWranglerExecution"] = false, // Critical specification requirement
            ["provenance"] = new JsonObject
            {
                ["sourceTool"] = attachment.SourceTool,
                ["importedAt"] = attachment.ImportTimeUtc.ToString("O"),
                ["contentHash"] = attachment.ContentHash,
                ["externalStatus"] = attachment.ExternalStatus,
                ["testMapping"] = attachment.TestMapping,
                ["environment"] = attachment.EnvironmentLabel
            },
            ["rawPayload"] = attachment.RawPayload
        };

        return json.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string DetectSourceTool(string payload)
    {
        var trimmed = payload.Trim();

        // 1. JUnit XML
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("<testsuite", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("<testsuites", StringComparison.OrdinalIgnoreCase))
        {
            return "JUnit";
        }

        // 2. Postman / Newman JSON run summary
        if (trimmed.StartsWith('{'))
        {
            if (trimmed.Contains("\"postman_id\"") || trimmed.Contains("\"collection\"") && trimmed.Contains("\"run\""))
            {
                return "Newman / Postman";
            }
        }

        // 3. Generic external tool
        return "External Runner";
    }

    private static string DetectStatus(string payload, string tool)
    {
        if (tool.Contains("JUnit", StringComparison.OrdinalIgnoreCase))
        {
            if (payload.Contains("failures=\"0\"") && payload.Contains("errors=\"0\""))
            {
                return "Passed";
            }
            if (payload.Contains("<failure") || payload.Contains("<error"))
            {
                return "Failed";
            }
        }
        else if (tool.Contains("Newman", StringComparison.OrdinalIgnoreCase) || tool.Contains("Postman", StringComparison.OrdinalIgnoreCase))
        {
            if (payload.Contains("\"failures\": 0") || payload.Contains("\"failures\":0"))
            {
                return "Passed";
            }
            if (payload.Contains("\"failures\":") && !payload.Contains("\"failures\": 0"))
            {
                return "Failed";
            }
        }

        return "Imported (External)";
    }
}
