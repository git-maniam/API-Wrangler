using System.Text.Json.Nodes;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed record BaselineRecord
{
    public int? StatusCode { get; init; }
    public IReadOnlyDictionary<string, string>? Headers { get; init; }
    public string? Body { get; init; }
    public IReadOnlyList<string> IgnoredPaths { get; init; } = [];
}

public sealed record BaselineDifference(
    string Aspect,
    string? Expected,
    string? Actual,
    string Message);

public sealed record BaselineComparisonResult(
    bool Matched,
    IReadOnlyList<BaselineDifference> Differences);

public static class BaselineEngine
{
    public static BaselineComparisonResult Compare(BaselineRecord baseline, HttpExecutionResult actual)
    {
        var diffs = new List<BaselineDifference>();

        // 1. Status Code
        if (baseline.StatusCode.HasValue && baseline.StatusCode.Value != (actual.StatusCode ?? 0))
        {
            diffs.Add(new BaselineDifference(
                "StatusCode",
                baseline.StatusCode.Value.ToString(),
                actual.StatusCode?.ToString(),
                $"Status code mismatch: expected {baseline.StatusCode}, got {actual.StatusCode}"));
        }

        // 2. Headers
        if (baseline.Headers is not null)
        {
            foreach (var (k, v) in baseline.Headers)
            {
                if (baseline.IgnoredPaths.Any(p => string.Equals(p, k, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var actualHeader = actual.Headers.FirstOrDefault(h => string.Equals(h.Name, k, StringComparison.OrdinalIgnoreCase));
                if (actualHeader is null)
                {
                    diffs.Add(new BaselineDifference(
                        $"Header:{k}",
                        v,
                        null,
                        $"Missing header '{k}' in response"));
                }
                else if (!string.Equals(actualHeader.Value, v, StringComparison.Ordinal))
                {
                    diffs.Add(new BaselineDifference(
                        $"Header:{k}",
                        v,
                        actualHeader.Value,
                        $"Header '{k}' value mismatch: expected '{v}', got '{actualHeader.Value}'"));
                }
            }
        }

        // 3. Body Comparison
        if (baseline.Body is not null)
        {
            var actualBody = actual.Preview ?? "";
            bool jsonParsed = false;

            try
            {
                var baseNode = JsonNode.Parse(baseline.Body);
                var actualNode = JsonNode.Parse(actualBody);

                if (baseNode is not null && actualNode is not null)
                {
                    jsonParsed = true;
                    CompareJsonNodes(baseNode, actualNode, "$", baseline.IgnoredPaths, diffs);
                }
            }
            catch
            {
                // Fall back to plain text comparison
            }

            if (!jsonParsed)
            {
                if (!string.Equals(baseline.Body, actualBody, StringComparison.Ordinal))
                {
                    diffs.Add(new BaselineDifference(
                        "Body",
                        baseline.Body.Length > 100 ? baseline.Body[..100] + "..." : baseline.Body,
                        actualBody.Length > 100 ? actualBody[..100] + "..." : actualBody,
                        "Response body did not match expected baseline text."));
                }
            }
        }

        return new BaselineComparisonResult(diffs.Count == 0, diffs);
    }

    private static void CompareJsonNodes(
        JsonNode? expected,
        JsonNode? actual,
        string currentPath,
        IReadOnlyList<string> ignoredPaths,
        List<BaselineDifference> diffs)
    {
        if (ignoredPaths.Any(p => PathMatches(currentPath, p)))
        {
            return;
        }

        if (expected is null && actual is null) return;

        if (expected is null || actual is null)
        {
            diffs.Add(new BaselineDifference(
                currentPath,
                expected?.ToJsonString(),
                actual?.ToJsonString(),
                $"Mismatch at path '{currentPath}': expected {expected?.ToJsonString() ?? "null"}, got {actual?.ToJsonString() ?? "null"}"));
            return;
        }

        if (expected is JsonObject expObj && actual is JsonObject actObj)
        {
            foreach (var prop in expObj)
            {
                var nextPath = currentPath == "$" ? $"$.{prop.Key}" : $"{currentPath}.{prop.Key}";
                if (ignoredPaths.Any(p => PathMatches(nextPath, p) || PathMatches(prop.Key, p)))
                {
                    continue;
                }

                if (!actObj.TryGetPropertyValue(prop.Key, out var actVal))
                {
                    diffs.Add(new BaselineDifference(
                        nextPath,
                        prop.Value?.ToJsonString(),
                        null,
                        $"Missing property '{prop.Key}' at '{currentPath}'"));
                }
                else
                {
                    CompareJsonNodes(prop.Value, actVal, nextPath, ignoredPaths, diffs);
                }
            }
            return;
        }

        if (expected is JsonArray expArr && actual is JsonArray actArr)
        {
            if (expArr.Count != actArr.Count)
            {
                diffs.Add(new BaselineDifference(
                    currentPath,
                    $"Array[{expArr.Count}]",
                    $"Array[{actArr.Count}]",
                    $"Array length mismatch at '{currentPath}': expected {expArr.Count}, got {actArr.Count}"));
                return;
            }

            for (int i = 0; i < expArr.Count; i++)
            {
                CompareJsonNodes(expArr[i], actArr[i], $"{currentPath}[{i}]", ignoredPaths, diffs);
            }
            return;
        }

        var expStr = expected.ToJsonString();
        var actStr = actual.ToJsonString();
        if (!string.Equals(expStr, actStr, StringComparison.Ordinal))
        {
            diffs.Add(new BaselineDifference(
                currentPath,
                expStr,
                actStr,
                $"Value mismatch at '{currentPath}': expected {expStr}, got {actStr}"));
        }
    }

    private static bool PathMatches(string actualPath, string ignoredPattern)
    {
        if (string.Equals(actualPath, ignoredPattern, StringComparison.OrdinalIgnoreCase))
            return true;

        if (actualPath.EndsWith("." + ignoredPattern, StringComparison.OrdinalIgnoreCase))
            return true;

        if (ignoredPattern.StartsWith("*.") && actualPath.EndsWith(ignoredPattern[1..], StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
