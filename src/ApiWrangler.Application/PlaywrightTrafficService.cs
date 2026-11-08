using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed class PlaywrightTrafficService
{
    private static readonly HashSet<string> SensitiveHeaderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "ApiKey",
        "Token",
        "X-Auth-Token",
        "Secret"
    };

    private static readonly HashSet<string> SensitiveBodyProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "password",
        "secret",
        "token",
        "accessToken",
        "refreshToken",
        "apiKey",
        "clientSecret",
        "credential",
        "privateKey"
    };

    public CapturedTrafficRecord RedactTraffic(CapturedTrafficRecord raw, int maxBodyBytes = 262_144)
    {
        // 1. Redact Request Headers
        var redactedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (raw.Headers is not null)
        {
            foreach (var (k, v) in raw.Headers)
            {
                redactedHeaders[k] = SensitiveHeaderKeys.Contains(k) ? "[REDACTED]" : v;
            }
        }

        // 2. Redact Response Headers
        var redactedResponseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (raw.ResponseHeaders is not null)
        {
            foreach (var (k, v) in raw.ResponseHeaders)
            {
                redactedResponseHeaders[k] = SensitiveHeaderKeys.Contains(k) ? "[REDACTED]" : v;
            }
        }

        // 3. Redact and bound request body
        bool isTruncated = raw.IsTruncated;
        string? redactedBody = RedactBody(raw.Body, maxBodyBytes, ref isTruncated);
        string? redactedResponseBody = RedactBody(raw.ResponseBody, maxBodyBytes, ref isTruncated);

        return raw with
        {
            Headers = redactedHeaders,
            ResponseHeaders = redactedResponseHeaders,
            Body = redactedBody,
            ResponseBody = redactedResponseBody,
            IsTruncated = isTruncated
        };
    }

    private static string? RedactBody(string? body, int maxBytes, ref bool isTruncated)
    {
        if (string.IsNullOrEmpty(body)) return body;

        string current = body;

        // Truncate if exceeds bound
        if (current.Length > maxBytes)
        {
            current = current[..maxBytes] + " [TRUNCATED]";
            isTruncated = true;
        }

        // Attempt JSON redaction
        try
        {
            var node = JsonNode.Parse(current);
            if (node is not null)
            {
                RedactJsonNode(node);
                return node.ToJsonString();
            }
        }
        catch
        {
            // Not valid JSON or truncated JSON; return bounded raw text
        }

        return current;
    }

    private static void RedactJsonNode(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            var keys = obj.Select(kv => kv.Key).ToList();
            foreach (var k in keys)
            {
                if (SensitiveBodyProperties.Contains(k))
                {
                    obj[k] = "[REDACTED]";
                }
                else if (obj[k] is not null)
                {
                    RedactJsonNode(obj[k]!);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is not null)
                {
                    RedactJsonNode(item);
                }
            }
        }
    }

    public TrafficMatchResult MatchTraffic(CapturedTrafficRecord record, IReadOnlyList<HttpRequestDefinition> knownRequests)
    {
        var candidates = new List<(HttpRequestDefinition Request, int Score)>();

        foreach (var req in knownRequests)
        {
            // Method match (case-insensitive)
            if (!string.Equals(req.Method, record.Method, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int score = 0;

            // Extract path from request definition
            string reqPath = ExtractPath(req.Url);
            string recPath = record.Path;

            // Exact path match
            if (string.Equals(reqPath, recPath, StringComparison.OrdinalIgnoreCase))
            {
                score = 100 + reqPath.Length;
            }
            // Parameterized template match e.g. /api/users/{id}
            else if (reqPath.Contains('{') && reqPath.Contains('}'))
            {
                string regexPattern = "^" + Regex.Replace(reqPath, @"\{[a-zA-Z0-9_]+\}", "([^/]+)") + "$";
                if (Regex.IsMatch(recPath, regexPattern, RegexOptions.IgnoreCase))
                {
                    score = 60 + reqPath.Length;
                }
            }
            // Wildcard match e.g. /api/users/*
            else if (reqPath.EndsWith("/*"))
            {
                string prefix = reqPath[..^2];
                if (recPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    score = 40 + prefix.Length;
                }
            }

            if (score > 0)
            {
                // Headers boost
                if (req.Headers is not null && record.Headers is not null)
                {
                    foreach (var h in req.Headers)
                    {
                        if (h.Enabled && record.Headers.TryGetValue(h.Name, out var recVal) &&
                            string.Equals(h.Value, recVal, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 10;
                        }
                    }
                }

                candidates.Add((req, score));
            }
        }

        if (candidates.Count == 0)
        {
            return new TrafficMatchResult(
                IsMatched: false,
                MatchedRequestId: null,
                MatchedRequestName: null,
                MatchedCollectionId: null,
                SpecificityScore: 0,
                IsAmbiguous: false,
                Explanations: [$"No collection request matched URL {record.Url}"]
            );
        }

        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));
        int maxScore = candidates[0].Score;
        var topCandidates = candidates.Where(c => c.Score == maxScore).ToList();

        if (topCandidates.Count > 1)
        {
            return new TrafficMatchResult(
                IsMatched: true,
                MatchedRequestId: topCandidates[0].Request.Id.ToString(),
                MatchedRequestName: topCandidates[0].Request.Name,
                MatchedCollectionId: null,
                SpecificityScore: maxScore,
                IsAmbiguous: true,
                AmbiguousCandidates: topCandidates.Select(c => c.Request.Id.ToString()).ToList(),
                Explanations: [$"Ambiguous match: {topCandidates.Count} collection requests matched equally with score {maxScore}."]
            );
        }

        var best = topCandidates[0];
        return new TrafficMatchResult(
            IsMatched: true,
            MatchedRequestId: best.Request.Id.ToString(),
            MatchedRequestName: best.Request.Name,
            MatchedCollectionId: null,
            SpecificityScore: best.Score,
            IsAmbiguous: false,
            Explanations: [$"Matched request '{best.Request.Name}' ({best.Request.Id}) with specificity score {best.Score}."]
        );
    }

    private static string ExtractPath(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "/";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Uri.UnescapeDataString(uri.AbsolutePath);
        }

        // Relative path
        int queryIndex = url.IndexOf('?');
        string path = queryIndex >= 0 ? url[..queryIndex] : url;
        return Uri.UnescapeDataString(path);
    }

    public CapturedTrafficValidationResult ValidateTrafficRecord(
        CapturedTrafficRecord record,
        HttpRequestDefinition? matchedDefinition,
        IReadOnlyList<TrafficAssertionRule>? customAssertions = null)
    {
        var match = matchedDefinition is not null
            ? new TrafficMatchResult(true, matchedDefinition.Id.ToString(), matchedDefinition.Name, null, 100)
            : new TrafficMatchResult(false, null, null, null, 0, Explanations: ["Unmatched"]);

        var failedAssertions = new List<string>();

        if (customAssertions is not null)
        {
            foreach (var rule in customAssertions)
            {
                if (!EvaluateAssertion(record, rule, out string failureReason))
                {
                    failedAssertions.Add(failureReason);
                }
            }
        }

        return new CapturedTrafficValidationResult(
            TrafficRecord: record,
            Match: match,
            AssertionsPassed: failedAssertions.Count == 0,
            FailedAssertions: failedAssertions
        );
    }

    private static bool EvaluateAssertion(CapturedTrafficRecord record, TrafficAssertionRule rule, out string failureReason)
    {
        failureReason = string.Empty;
        string prop = rule.TargetProperty.Trim();

        // 1. Status Code
        if (prop.Equals("StatusCode", StringComparison.OrdinalIgnoreCase))
        {
            int actual = record.ResponseStatusCode;
            if (!int.TryParse(rule.ExpectedValue, out int expected))
            {
                failureReason = $"Invalid expected status code '{rule.ExpectedValue}'";
                return false;
            }

            bool ok = rule.Operator.ToLowerInvariant() switch
            {
                "equals" or "==" => actual == expected,
                "notequals" or "!=" => actual != expected,
                "lessthan" or "<" => actual < expected,
                "greaterthan" or ">" => actual > expected,
                _ => actual == expected
            };

            if (!ok)
            {
                failureReason = $"Expected StatusCode {rule.Operator} {expected}, but received {actual}.";
                return false;
            }

            return true;
        }

        // 2. Header
        if (prop.StartsWith("Header.", StringComparison.OrdinalIgnoreCase))
        {
            string headerName = prop["Header.".Length..];
            string actual = record.ResponseHeaders != null && record.ResponseHeaders.TryGetValue(headerName, out var v) ? v : "";

            bool ok = EvaluateStringComparison(actual, rule.Operator, rule.ExpectedValue);
            if (!ok)
            {
                failureReason = $"Expected Header '{headerName}' {rule.Operator} '{rule.ExpectedValue}', but received '{actual}'.";
                return false;
            }

            return true;
        }

        // 3. Body JSON property
        string jsonPath = prop.StartsWith("Body.", StringComparison.OrdinalIgnoreCase) ? prop["Body.".Length..] : prop;
        if (!string.IsNullOrEmpty(record.ResponseBody))
        {
            try
            {
                var doc = JsonDocument.Parse(record.ResponseBody);
                if (TryResolveJsonProperty(doc.RootElement, jsonPath, out var jsonElement))
                {
                    string actual = jsonElement.ToString();

                    // If numeric comparison
                    if ((rule.Operator.Equals("GreaterThan", StringComparison.OrdinalIgnoreCase) ||
                         rule.Operator.Equals("LessThan", StringComparison.OrdinalIgnoreCase)) &&
                        jsonElement.TryGetDouble(out double numActual) &&
                        double.TryParse(rule.ExpectedValue, out double numExpected))
                    {
                        bool numOk = rule.Operator.Equals("GreaterThan", StringComparison.OrdinalIgnoreCase)
                            ? numActual > numExpected
                            : numActual < numExpected;

                        if (!numOk)
                        {
                            failureReason = $"Expected {prop} {rule.Operator} {numExpected}, but received {numActual}.";
                            return false;
                        }

                        return true;
                    }

                    bool ok = EvaluateStringComparison(actual, rule.Operator, rule.ExpectedValue);
                    if (!ok)
                    {
                        failureReason = $"Expected {prop} {rule.Operator} '{rule.ExpectedValue}', but received '{actual}'.";
                        return false;
                    }

                    return true;
                }
                else
                {
                    failureReason = $"Property '{jsonPath}' not found in response body.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                failureReason = $"Failed to parse response body for property '{jsonPath}': {ex.Message}";
                return false;
            }
        }

        failureReason = $"Cannot evaluate {prop}: response body was empty.";
        return false;
    }

    private static bool TryResolveJsonProperty(JsonElement element, string path, out JsonElement result)
    {
        result = element;
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var curr = element;

        foreach (var seg in segments)
        {
            if (curr.ValueKind != JsonValueKind.Object || !curr.TryGetProperty(seg, out curr))
            {
                result = default;
                return false;
            }
        }

        result = curr;
        return true;
    }

    private static bool EvaluateStringComparison(string actual, string op, string expected)
    {
        return op.ToLowerInvariant() switch
        {
            "equals" or "==" => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "notequals" or "!=" => !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "contains" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "notempty" => !string.IsNullOrWhiteSpace(actual),
            "regex" => Regex.IsMatch(actual, expected, RegexOptions.IgnoreCase),
            _ => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
        };
    }

    public PlaywrightCombinedScenarioResult EvaluateScenario(
        string scenarioName,
        bool uiPassed,
        IReadOnlyList<CapturedTrafficRecord> traffic,
        IReadOnlyList<HttpRequestDefinition> knownRequests,
        IReadOnlyList<TrafficAssertionRule>? customAssertions = null)
    {
        var validationResults = new List<CapturedTrafficValidationResult>();
        var unmatchedTraffic = new List<CapturedTrafficRecord>();
        var ambiguousTraffic = new List<CapturedTrafficRecord>();
        var limitations = new HashSet<string>();

        foreach (var raw in traffic)
        {
            var record = RedactTraffic(raw);

            // Detect explicit limitations
            if (record.IsCached)
            {
                limitations.Add("BrowserHttpCache: One or more responses were served from browser cache; network payload may not reflect live server changes.");
            }
            if (record.IsServiceWorker)
            {
                limitations.Add("ServiceWorkerInterception: Traffic was intercepted or synthesized by a Service Worker.");
            }
            if (record.IsWebSocket)
            {
                limitations.Add("WebSocketTraffic: WebSocket traffic requires dedicated framing validation.");
            }
            if (record.IsTruncated)
            {
                limitations.Add("BoundedPayloadLimit: Payloads exceeding the configured limit were truncated.");
            }

            var match = MatchTraffic(record, knownRequests);

            if (!match.IsMatched)
            {
                unmatchedTraffic.Add(record);
            }
            else if (match.IsAmbiguous)
            {
                ambiguousTraffic.Add(record);
            }

            HttpRequestDefinition? matchedReq = match.IsMatched && !match.IsAmbiguous
                ? knownRequests.FirstOrDefault(r => r.Id.ToString() == match.MatchedRequestId)
                : null;

            var val = ValidateTrafficRecord(record, matchedReq, customAssertions);
            validationResults.Add(val);
        }

        bool apiPassed = validationResults.All(v => v.AssertionsPassed);
        bool combinedPassed = uiPassed && apiPassed;

        string failureSource = (!uiPassed && !apiPassed) ? "Both"
                             : (!uiPassed ? "UI"
                             : (!apiPassed ? "API" : "None"));

        string? primaryFailure = null;
        if (!apiPassed)
        {
            var failing = validationResults.Where(v => !v.AssertionsPassed).SelectMany(v => v.FailedAssertions).ToList();
            primaryFailure = $"API assertion failure in scenario '{scenarioName}': {string.Join("; ", failing)}";
        }
        else if (!uiPassed)
        {
            primaryFailure = $"UI assertion failure in scenario '{scenarioName}'.";
        }

        return new PlaywrightCombinedScenarioResult(
            ScenarioName: scenarioName,
            UiPassed: uiPassed,
            ApiPassed: apiPassed,
            CombinedPassed: combinedPassed,
            FailureSource: failureSource,
            ValidationResults: validationResults,
            UnmatchedTraffic: unmatchedTraffic,
            AmbiguousTraffic: ambiguousTraffic,
            LimitationsEncountered: limitations.ToList(),
            PrimaryFailureMessage: primaryFailure
        );
    }

    public TrafficDependencyMap BuildDependencyMap(string scenarioName, IReadOnlyList<CapturedTrafficRecord> traffic)
    {
        var endpoints = new List<string>();
        var edgeCounts = new Dictionary<(string, string), int>();

        string previous = scenarioName;

        foreach (var t in traffic)
        {
            string endpoint = $"{t.Method.ToUpperInvariant()} {t.Path}";
            if (!endpoints.Contains(endpoint))
            {
                endpoints.Add(endpoint);
            }

            var key = (previous, endpoint);
            edgeCounts[key] = edgeCounts.TryGetValue(key, out int count) ? count + 1 : 1;
            previous = endpoint;
        }

        var edges = edgeCounts.Select(kv => new DependencyEdge(kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();
        return new TrafficDependencyMap(scenarioName, endpoints, edges);
    }

    public IReadOnlyList<HttpRequestDefinition> GenerateDraftRequests(IReadOnlyList<CapturedTrafficRecord> unmatchedTraffic)
    {
        var drafts = new List<HttpRequestDefinition>();

        foreach (var rec in unmatchedTraffic)
        {
            Guid draftId = Guid.NewGuid();
            string name = $"Draft: {rec.Method.ToUpperInvariant()} {rec.Path}";

            var headersList = rec.Headers is not null
                ? rec.Headers.Select(kv => new HeaderEntry(kv.Key, kv.Value, true)).ToList()
                : new List<HeaderEntry>();

            drafts.Add(new HttpRequestDefinition
            {
                Id = draftId,
                Name = name,
                Method = rec.Method.ToUpperInvariant(),
                Url = rec.Url,
                Headers = headersList,
                Body = rec.Body
            });
        }

        return drafts;
    }

    public void GenerateCiArtifacts(PlaywrightSummaryReport summary, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        // 1. JSON Report
        string jsonPath = Path.Combine(outputDirectory, "playwright-api-report.json");
        var opts = new JsonSerializerOptions { WriteIndented = true };
        string jsonText = JsonSerializer.Serialize(summary, opts);
        File.WriteAllText(jsonPath, jsonText);

        // 2. Markdown Summary
        string mdPath = Path.Combine(outputDirectory, "playwright-api-summary.md");
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Playwright & API Combined Test Summary");
        sb.AppendLine();
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|---|---|");
        sb.AppendLine($"| Total Scenarios | {summary.TotalScenarios} |");
        sb.AppendLine($"| Passed Scenarios | {summary.PassedScenarios} |");
        sb.AppendLine($"| Failed Scenarios | {summary.FailedScenarios} |");
        sb.AppendLine($"| Total Captured Traffic | {summary.TotalCapturedRequests} |");
        sb.AppendLine($"| Matched Requests | {summary.MatchedRequests} |");
        sb.AppendLine($"| Unmatched Requests | {summary.UnmatchedRequests} |");
        sb.AppendLine($"| Ambiguous Matches | {summary.AmbiguousRequests} |");
        sb.AppendLine($"| Total Assertion Failures | {summary.TotalAssertionFailures} |");
        sb.AppendLine();

        sb.AppendLine("## Scenarios Breakdown");
        sb.AppendLine();
        sb.AppendLine("| Scenario | UI Status | API Status | Combined Outcome | Failure Source | Details |");
        sb.AppendLine("|---|---|---|---|---|---|");

        foreach (var s in summary.Scenarios)
        {
            string outcome = s.CombinedPassed ? "PASSED" : "FAILED";
            string details = s.PrimaryFailureMessage ?? "All UI & API assertions satisfied.";
            sb.AppendLine($"| {s.ScenarioName} | {(s.UiPassed ? "Pass" : "Fail")} | {(s.ApiPassed ? "Pass" : "Fail")} | **{outcome}** | {s.FailureSource} | {details} |");
        }

        File.WriteAllText(mdPath, sb.ToString());
    }
}
