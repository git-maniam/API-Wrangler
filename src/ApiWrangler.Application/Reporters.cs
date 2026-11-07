using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public static class EvidenceSanitizer
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "api-key"
    };

    public static IReadOnlyList<HeaderEntry> SanitizeHeaders(IReadOnlyList<HeaderEntry> headers)
    {
        var sanitized = new List<HeaderEntry>(headers.Count);
        foreach (var h in headers)
        {
            if (SensitiveHeaders.Contains(h.Name) || h.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) || h.Name.Contains("token", StringComparison.OrdinalIgnoreCase))
            {
                sanitized.Add(new HeaderEntry(h.Name, "[REDACTED]", h.Enabled));
            }
            else
            {
                sanitized.Add(h);
            }
        }
        return sanitized;
    }

    public static string SanitizeText(string text, IEnumerable<string>? knownSecrets = null)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var result = text;
        if (knownSecrets is not null)
        {
            foreach (var secret in knownSecrets.Where(s => !string.IsNullOrEmpty(s)))
            {
                result = result.Replace(secret, "[REDACTED]");
            }
        }

        // Redact standard bearer tokens and keys
        result = Regex.Replace(result, @"(?i)(bearer\s+)[A-Za-z0-9_\-\.]{15,}", "$1[REDACTED]");
        result = Regex.Replace(result, @"(?i)(""?(?:password|token|secret|client_secret|apikey)""?\s*[:=]\s*""?)[^"",\s\}]+", "$1[REDACTED]");

        return result;
    }
}

public interface IRunReporter
{
    string FormatName { get; }
    string GenerateReport(CollectionExecutionReport report, IEnumerable<string>? knownSecrets = null);
}

public sealed class ConsoleReporter : IRunReporter
{
    public string FormatName => "console";

    public string GenerateReport(CollectionExecutionReport report, IEnumerable<string>? knownSecrets = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== Collection Run: {report.CollectionName} [{report.Status}] ===");
        sb.AppendLine($"Run ID: {report.RunId} | Duration: {report.Duration.TotalMilliseconds:F0}ms");
        sb.AppendLine($"Requests: Total={report.TotalRequestsRun}, Passed={report.PassedRequests}, Failed={report.FailedRequests}");

        if (report.SetupResults.Count > 0)
        {
            sb.AppendLine("--- Setup ---");
            foreach (var s in report.SetupResults)
            {
                FormatRequest(sb, s, "Setup");
            }
        }

        foreach (var iter in report.Iterations)
        {
            sb.AppendLine($"--- Iteration {iter.IterationIndex} [{iter.Status}] ({iter.Duration.TotalMilliseconds:F0}ms) ---");
            foreach (var req in iter.Requests)
            {
                FormatRequest(sb, req, $"Iter {iter.IterationIndex}");
            }
        }

        if (report.TeardownResults.Count > 0)
        {
            sb.AppendLine("--- Teardown ---");
            foreach (var t in report.TeardownResults)
            {
                FormatRequest(sb, t, "Teardown");
            }
        }

        if (!string.IsNullOrEmpty(report.Error))
        {
            sb.AppendLine($"Error: {report.Error}");
        }

        return EvidenceSanitizer.SanitizeText(sb.ToString(), knownSecrets);
    }

    private static void FormatRequest(StringBuilder sb, RequestExecutionRecord req, string scope)
    {
        var statusStr = req.StatusCode.HasValue ? $"HTTP {req.StatusCode}" : "-";
        sb.AppendLine($"  [{req.Outcome}] {req.RequestName} ({statusStr}, {req.Duration.TotalMilliseconds:F0}ms)");

        if (!string.IsNullOrEmpty(req.ResponsePreview))
        {
            sb.AppendLine($"    Preview: {req.ResponsePreview}");
        }

        if (req.Assertions.Count > 0)
        {
            int passedA = req.Assertions.Count(a => a.Passed);
            sb.AppendLine($"    Assertions: {passedA}/{req.Assertions.Count} passed");
            foreach (var a in req.Assertions.Where(a => !a.Passed))
            {
                sb.AppendLine($"      FAIL: {a.Name} -> {a.Message}");
            }
        }

        if (req.Outcome != ExecutionOutcome.Passed && !string.IsNullOrEmpty(req.Error))
        {
            sb.AppendLine($"    Error: {req.Error}");
        }
    }
}

public sealed class JsonReporter : IRunReporter
{
    public string FormatName => "json";

    public string GenerateReport(CollectionExecutionReport report, IEnumerable<string>? knownSecrets = null)
    {
        var node = new JsonObject
        {
            ["runId"] = report.RunId.ToString(),
            ["collectionId"] = report.CollectionId.ToString(),
            ["collectionName"] = report.CollectionName,
            ["status"] = report.Status.ToString(),
            ["startTimeUtc"] = report.StartTimeUtc.ToString("o"),
            ["endTimeUtc"] = report.EndTimeUtc.ToString("o"),
            ["durationMs"] = report.Duration.TotalMilliseconds,
            ["engineVersion"] = report.EngineVersion,
            ["osArchitecture"] = report.OsArchitecture,
            ["gitCommit"] = report.GitCommit,
            ["isDirty"] = report.IsDirty,
            ["snapshotHash"] = report.SnapshotHash,
            ["environmentName"] = report.EnvironmentName,
            ["seed"] = report.Seed,
            ["totalRequestsRun"] = report.TotalRequestsRun,
            ["passedRequests"] = report.PassedRequests,
            ["failedRequests"] = report.FailedRequests,
            ["error"] = report.Error
        };

        var setupArr = new JsonArray();
        foreach (var s in report.SetupResults) setupArr.Add(SerializeRequestRecord(s));
        node["setupResults"] = setupArr;

        var iterArr = new JsonArray();
        foreach (var iter in report.Iterations)
        {
            var iObj = new JsonObject
            {
                ["iterationIndex"] = iter.IterationIndex,
                ["status"] = iter.Status.ToString(),
                ["durationMs"] = iter.Duration.TotalMilliseconds,
                ["error"] = iter.Error
            };
            var reqArr = new JsonArray();
            foreach (var r in iter.Requests) reqArr.Add(SerializeRequestRecord(r));
            iObj["requests"] = reqArr;
            iterArr.Add(iObj);
        }
        node["iterations"] = iterArr;

        var tdArr = new JsonArray();
        foreach (var t in report.TeardownResults) tdArr.Add(SerializeRequestRecord(t));
        node["teardownResults"] = tdArr;

        var json = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return EvidenceSanitizer.SanitizeText(json, knownSecrets);
    }

    private static JsonObject SerializeRequestRecord(RequestExecutionRecord r)
    {
        var obj = new JsonObject
        {
            ["requestId"] = r.RequestId.ToString(),
            ["requestName"] = r.RequestName,
            ["iteration"] = r.Iteration,
            ["attempt"] = r.Attempt,
            ["outcome"] = r.Outcome.ToString(),
            ["statusCode"] = r.StatusCode,
            ["durationMs"] = r.Duration.TotalMilliseconds,
            ["error"] = r.Error,
            ["responsePreview"] = r.ResponsePreview,
            ["bodyPath"] = r.BodyPath
        };

        if (r.Assertions.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var a in r.Assertions)
            {
                arr.Add(new JsonObject
                {
                    ["assertionId"] = a.AssertionId.ToString(),
                    ["name"] = a.Name,
                    ["passed"] = a.Passed,
                    ["actualValue"] = a.ActualValue,
                    ["expectedValue"] = a.ExpectedValue,
                    ["message"] = a.Message
                });
            }
            obj["assertions"] = arr;
        }

        return obj;
    }
}

public sealed class JUnitReporter : IRunReporter
{
    public string FormatName => "junit";

    public string GenerateReport(CollectionExecutionReport report, IEnumerable<string>? knownSecrets = null)
    {
        var sb = new StringBuilder();
        int totalTests = report.TotalRequestsRun;
        int failures = report.Iterations.Sum(i => i.Requests.Count(r => r.Outcome == ExecutionOutcome.Failed)) +
                       report.SetupResults.Count(r => r.Outcome == ExecutionOutcome.Failed) +
                       report.TeardownResults.Count(r => r.Outcome == ExecutionOutcome.Failed);
        int errors = report.Iterations.Sum(i => i.Requests.Count(r => r.Outcome == ExecutionOutcome.Error)) +
                     report.SetupResults.Count(r => r.Outcome == ExecutionOutcome.Error) +
                     report.TeardownResults.Count(r => r.Outcome == ExecutionOutcome.Error);
        int skipped = totalTests - (report.PassedRequests + failures + errors);

        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine($"<testsuites name=\"{SecurityElement.Escape(report.CollectionName)}\" tests=\"{totalTests}\" failures=\"{failures}\" errors=\"{errors}\" time=\"{report.Duration.TotalSeconds:F3}\">");

        // Iterations as test suites
        foreach (var iter in report.Iterations)
        {
            int iterF = iter.Requests.Count(r => r.Outcome == ExecutionOutcome.Failed);
            int iterE = iter.Requests.Count(r => r.Outcome == ExecutionOutcome.Error);
            sb.AppendLine($"  <testsuite name=\"Iteration {iter.IterationIndex}\" tests=\"{iter.Requests.Count}\" failures=\"{iterF}\" errors=\"{iterE}\" time=\"{iter.Duration.TotalSeconds:F3}\">");

            foreach (var r in iter.Requests)
            {
                FormatTestCase(sb, r, report.CollectionName);
            }

            sb.AppendLine("  </testsuite>");
        }

        // Setup/teardown suites if present
        if (report.SetupResults.Count > 0)
        {
            sb.AppendLine($"  <testsuite name=\"Setup\" tests=\"{report.SetupResults.Count}\">");
            foreach (var r in report.SetupResults) FormatTestCase(sb, r, $"{report.CollectionName}.Setup");
            sb.AppendLine("  </testsuite>");
        }

        if (report.TeardownResults.Count > 0)
        {
            sb.AppendLine($"  <testsuite name=\"Teardown\" tests=\"{report.TeardownResults.Count}\">");
            foreach (var r in report.TeardownResults) FormatTestCase(sb, r, $"{report.CollectionName}.Teardown");
            sb.AppendLine("  </testsuite>");
        }

        sb.AppendLine("</testsuites>");

        return EvidenceSanitizer.SanitizeText(sb.ToString(), knownSecrets);
    }

    private static void FormatTestCase(StringBuilder sb, RequestExecutionRecord r, string className)
    {
        sb.AppendLine($"    <testcase classname=\"{SecurityElement.Escape(className)}\" name=\"{SecurityElement.Escape(r.RequestName)}\" time=\"{r.Duration.TotalSeconds:F3}\">");

        if (r.Outcome == ExecutionOutcome.Failed)
        {
            var msg = SecurityElement.Escape(r.Error ?? "Assertion failure");
            sb.AppendLine($"      <failure message=\"{msg}\" type=\"AssertionFailure\">{msg}</failure>");
        }
        else if (r.Outcome == ExecutionOutcome.Error)
        {
            var msg = SecurityElement.Escape(r.Error ?? "Transport error");
            sb.AppendLine($"      <error message=\"{msg}\" type=\"TransportError\">{msg}</error>");
        }
        else if (r.Outcome is ExecutionOutcome.Skipped or ExecutionOutcome.NotRun or ExecutionOutcome.Cancelled)
        {
            sb.AppendLine("      <skipped />");
        }

        sb.AppendLine("    </testcase>");
    }
}

public sealed class MarkdownReporter : IRunReporter
{
    public string FormatName => "markdown";

    public string GenerateReport(CollectionExecutionReport report, IEnumerable<string>? knownSecrets = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Collection Run Report: {report.CollectionName}");
        sb.AppendLine();
        sb.AppendLine($"- **Status:** `{report.Status}`");
        sb.AppendLine($"- **Duration:** {report.Duration.TotalMilliseconds:F0} ms");
        sb.AppendLine($"- **Total Requests:** {report.TotalRequestsRun} (Passed: {report.PassedRequests}, Failed: {report.FailedRequests})");
        sb.AppendLine($"- **Start Time:** {report.StartTimeUtc:u}");
        sb.AppendLine($"- **OS/Arch:** {report.OsArchitecture}");
        sb.AppendLine();

        sb.AppendLine("| Scope | Request | Status Code | Outcome | Duration | Assertions | Details |");
        sb.AppendLine("|---|---|---|---|---|---|---|");

        foreach (var s in report.SetupResults)
        {
            AppendRow(sb, "Setup", s);
        }

        foreach (var iter in report.Iterations)
        {
            foreach (var r in iter.Requests)
            {
                AppendRow(sb, $"Iter {iter.IterationIndex}", r);
            }
        }

        foreach (var t in report.TeardownResults)
        {
            AppendRow(sb, "Teardown", t);
        }

        sb.AppendLine();
        return EvidenceSanitizer.SanitizeText(sb.ToString(), knownSecrets);
    }

    private static void AppendRow(StringBuilder sb, string scope, RequestExecutionRecord r)
    {
        var code = r.StatusCode.HasValue ? r.StatusCode.Value.ToString() : "-";
        var assertions = r.Assertions.Count > 0 ? $"{r.Assertions.Count(a => a.Passed)}/{r.Assertions.Count}" : "-";
        var details = (r.Error ?? "").Replace("|", "\\|").Replace("\n", " ");
        sb.AppendLine($"| {scope} | {r.RequestName} | {code} | {r.Outcome} | {r.Duration.TotalMilliseconds:F0} ms | {assertions} | {details} |");
    }
}

public sealed class HtmlReporter : IRunReporter
{
    public string FormatName => "html";

    public string GenerateReport(CollectionExecutionReport report, IEnumerable<string>? knownSecrets = null)
    {
        var statusColor = report.Status switch
        {
            RunStatus.Passed => "#107c41",
            RunStatus.Failed => "#d83b01",
            RunStatus.Error => "#a80000",
            RunStatus.Cancelled => "#5c5c5c",
            _ => "#0078d4"
        };

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\" />");
        sb.AppendLine($"<title>Run Report - {SecurityElement.Escape(report.CollectionName)}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif; margin: 24px; background: #f8f9fa; color: #212529; }");
        sb.AppendLine(".card { background: #fff; border-radius: 8px; padding: 20px; box-shadow: 0 1px 4px rgba(0,0,0,0.1); margin-bottom: 20px; }");
        sb.AppendLine(".badge { display: inline-block; padding: 4px 12px; border-radius: 12px; color: #fff; font-weight: bold; font-size: 13px; }");
        sb.AppendLine("table { width: 100%; border-collapse: collapse; margin-top: 12px; }");
        sb.AppendLine("th, td { text-align: left; padding: 10px 12px; border-bottom: 1px solid #e9ecef; }");
        sb.AppendLine("th { background: #f1f3f5; }");
        sb.AppendLine(".fail { color: #d83b01; font-weight: 600; }");
        sb.AppendLine(".pass { color: #107c41; font-weight: 600; }");
        sb.AppendLine(".error { color: #a80000; font-weight: 600; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine("<div class=\"card\">");
        sb.AppendLine($"<h1>Collection Report: {SecurityElement.Escape(report.CollectionName)} <span class=\"badge\" style=\"background: {statusColor};\">{report.Status}</span></h1>");
        sb.AppendLine($"<p>Run ID: <code>{report.RunId}</code> | Duration: <strong>{report.Duration.TotalMilliseconds:F0} ms</strong></p>");
        sb.AppendLine($"<p>Requests: Total=<strong>{report.TotalRequestsRun}</strong>, Passed=<strong>{report.PassedRequests}</strong>, Failed=<strong>{report.FailedRequests}</strong></p>");
        sb.AppendLine($"<p><small>Executed: {report.StartTimeUtc:u} | Engine: {report.EngineVersion} | System: {SecurityElement.Escape(report.OsArchitecture ?? "")}</small></p>");
        sb.AppendLine("</div>");

        sb.AppendLine("<div class=\"card\">");
        sb.AppendLine("<h2>Execution Results</h2>");
        sb.AppendLine("<table>");
        sb.AppendLine("<tr><th>Scope</th><th>Request</th><th>Status</th><th>Outcome</th><th>Duration</th><th>Assertions</th><th>Details</th></tr>");

        foreach (var s in report.SetupResults) AppendHtmlRow(sb, "Setup", s);
        foreach (var iter in report.Iterations)
        {
            foreach (var r in iter.Requests) AppendHtmlRow(sb, $"Iter {iter.IterationIndex}", r);
        }
        foreach (var t in report.TeardownResults) AppendHtmlRow(sb, "Teardown", t);

        sb.AppendLine("</table>");
        sb.AppendLine("</div>");

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return EvidenceSanitizer.SanitizeText(sb.ToString(), knownSecrets);
    }

    private static void AppendHtmlRow(StringBuilder sb, string scope, RequestExecutionRecord r)
    {
        var outcomeClass = r.Outcome switch
        {
            ExecutionOutcome.Passed => "pass",
            ExecutionOutcome.Failed => "fail",
            ExecutionOutcome.Error => "error",
            _ => ""
        };

        var code = r.StatusCode.HasValue ? r.StatusCode.Value.ToString() : "-";
        var assertions = r.Assertions.Count > 0 ? $"{r.Assertions.Count(a => a.Passed)}/{r.Assertions.Count}" : "-";
        sb.AppendLine($"<tr><td>{SecurityElement.Escape(scope)}</td><td><strong>{SecurityElement.Escape(r.RequestName)}</strong></td><td>{code}</td><td class=\"{outcomeClass}\">{r.Outcome}</td><td>{r.Duration.TotalMilliseconds:F0} ms</td><td>{assertions}</td><td><small>{SecurityElement.Escape(r.Error ?? "")}</small></td></tr>");
    }
}
