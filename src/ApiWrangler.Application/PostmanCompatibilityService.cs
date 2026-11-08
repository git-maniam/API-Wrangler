using System.Text.RegularExpressions;

namespace ApiWrangler.Application;

public sealed record PostmanFeatureDetection(
    string FeatureName,
    bool IsSupported,
    string? Notes
);

public sealed record PostmanCompatibilityReport(
    bool IsCompatible,
    IReadOnlyList<string> SupportedFeatures,
    IReadOnlyList<string> UnsupportedFeatures,
    string Summary
);

public sealed class PostmanCompatibilityService
{
    private static readonly (string Pattern, string FeatureName, bool IsSupported, string? Note)[] FeatureRules =
    [
        // Supported APIs
        (@"\bpm\.test\b", "pm.test", true, "Fully supported"),
        (@"\bpm\.expect\b", "pm.expect", true, "Supported with BDD / Chai assertion matchers"),
        (@"\bpm\.response\b", "pm.response", true, "Supported status, headers, json(), text(), responseTime"),
        (@"\bpm\.request\b", "pm.request", true, "Supported url, method, headers, body"),
        (@"\bpm\.environment\b", "pm.environment", true, "Supported get, set, has, unset, clear"),
        (@"\bpm\.collectionVariables\b", "pm.collectionVariables", true, "Supported get, set, has, unset, clear"),
        (@"\bpm\.variables\b", "pm.variables", true, "Supported hierarchical get, set, has"),
        (@"\bpm\.iterationData\b", "pm.iterationData", true, "Supported get, has, toObject"),
        (@"\bpm\.sendRequest\b", "pm.sendRequest", true, "Supported secondary HTTP requests"),
        (@"\bpostman\.setNextRequest\b", "postman.setNextRequest", true, "Supported flow control"),
        (@"\bpostman\.(set|get|clear)EnvironmentVariable\b", "postman.environmentLegacy", true, "Supported legacy environment variables"),
        (@"\bpostman\.(set|get|clear)GlobalVariable\b", "postman.globalsLegacy", true, "Supported legacy global variables"),
        (@"\btests\s*\[", "legacy.tests", true, "Supported legacy tests array/dictionary"),
        (@"\bresponse(Code|Body|Time|Headers)\b", "legacy.responseGlobals", true, "Supported legacy response globals"),

        // Unsupported APIs
        (@"\bpm\.visualizer\b", "pm.visualizer", false, "Visualizer HTML rendering is not supported; use workbench structured viewer."),
        (@"\bpm\.cookies\b", "pm.cookies", false, "Programmatic cookie jar is not supported; use standard HTTP cookie handling."),
        (@"\bpm\.execution\.skipRequest\b", "pm.execution.skipRequest", false, "Dynamic skipRequest is not supported; configure folder/collection lifecycle selection.")
    ];

    public PostmanCompatibilityReport AnalyzeScript(string scriptCode)
    {
        if (string.IsNullOrWhiteSpace(scriptCode))
        {
            return new PostmanCompatibilityReport(
                IsCompatible: true,
                SupportedFeatures: Array.Empty<string>(),
                UnsupportedFeatures: Array.Empty<string>(),
                Summary: "Empty script; fully compatible."
            );
        }

        var supported = new HashSet<string>(StringComparer.Ordinal);
        var unsupported = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (pattern, featureName, isSupported, note) in FeatureRules)
        {
            if (Regex.IsMatch(scriptCode, pattern))
            {
                if (isSupported)
                {
                    supported.Add(featureName);
                }
                else
                {
                    unsupported.Add($"{featureName}: {note}");
                }
            }
        }

        bool isCompatible = unsupported.Count == 0;
        string summary = isCompatible
            ? $"Script is compatible with API Wrangler ({supported.Count} Postman feature(s) detected)."
            : $"Script contains {unsupported.Count} unsupported Postman API(s) that require review or migration.";

        return new PostmanCompatibilityReport(
            IsCompatible: isCompatible,
            SupportedFeatures: supported.OrderBy(s => s, StringComparer.Ordinal).ToList(),
            UnsupportedFeatures: unsupported.OrderBy(u => u, StringComparer.Ordinal).ToList(),
            Summary: summary
        );
    }
}
