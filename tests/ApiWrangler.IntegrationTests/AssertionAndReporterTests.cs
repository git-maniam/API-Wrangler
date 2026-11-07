using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class AssertionAndReporterTests
{
    [Fact]
    public void AssertionEngine_StatusAndHeaderAndBody_EvaluatesCorrectly()
    {
        var headers = new List<HeaderEntry>
        {
            new("Content-Type", "application/json; charset=utf-8"),
            new("X-Custom-Trace", "trace-12345")
        };

        var cookies = new List<CookieEntry>
        {
            new("session_id", "abcde12345")
        };

        var bodyJson = "{\"status\":\"ok\",\"user\":{\"id\":42,\"name\":\"Alice\"},\"items\":[{\"id\":1},{\"id\":2}]}";

        var result = new HttpExecutionResult(
            ExecutionOutcome.Passed,
            200,
            "OK",
            headers,
            bodyJson,
            (long)bodyJson.Length,
            false,
            null,
            TimeSpan.FromMilliseconds(50),
            null,
            cookies);

        var assertions = new List<RequestAssertionDefinition>
        {
            new() { Type = "status", Operator = "equals", Expected = 200 },
            new() { Type = "status", Operator = "lessThan", Expected = 400 },
            new() { Type = "header", Target = "content-type", Operator = "contains", Expected = "application/json" },
            new() { Type = "header", Target = "X-Custom-Trace", Operator = "exists" },
            new() { Type = "cookie", Target = "session_id", Operator = "equals", Expected = "abcde12345" },
            new() { Type = "bodyContains", Operator = "contains", Expected = "Alice" },
            new() { Type = "jsonPath", Target = "$.user.id", Operator = "equals", Expected = 42 },
            new() { Type = "jsonPath", Target = "items[1].id", Operator = "equals", Expected = 2 },
            new() { Type = "responseTime", Operator = "lessThan", Expected = 500 },
            new() { Type = "regex", Expected = "\"status\":\\s*\"ok\"" }
        };

        var evalResults = AssertionEngine.EvaluateAll(assertions, result);

        Assert.Equal(assertions.Count, evalResults.Count);
        Assert.All(evalResults, a => Assert.True(a.Passed, $"Assertion failed: {a.Name} - {a.Message}"));
    }

    [Fact]
    public void FailedAssertion_ProducesFailedOutcome_DistinctFromTransportError()
    {
        // 1. Successful HTTP transport but failing assertion
        var successTransport = new HttpExecutionResult(
            ExecutionOutcome.Passed,
            200,
            "OK",
            [],
            "{\"status\":\"not_ok\"}",
            20,
            false,
            null,
            TimeSpan.FromMilliseconds(20),
            null);

        var assertion = new RequestAssertionDefinition
        {
            Type = "jsonPath",
            Target = "status",
            Operator = "equals",
            Expected = "ok"
        };

        var assertionRes = AssertionEngine.Evaluate(assertion, successTransport);
        Assert.False(assertionRes.Passed);
        Assert.Equal("status", assertion.Target);

        // 2. Transport error (e.g. connection refused)
        var transportError = new HttpExecutionResult(
            ExecutionOutcome.Error,
            null,
            null,
            [],
            "",
            0,
            false,
            null,
            TimeSpan.FromMilliseconds(10),
            "Connection refused to remote host");

        var transportEval = AssertionEngine.Evaluate(assertion, transportError);
        Assert.False(transportEval.Passed);
        Assert.Contains("Not evaluated due to request outcome: Error", transportEval.Message);
    }

    [Fact]
    public void BaselineComparator_ComparesAndIgnoresConfiguredPaths()
    {
        var baseline = new BaselineRecord
        {
            StatusCode = 200,
            Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body = "{\"id\":\"1111\",\"timestamp\":\"2026-11-01T00:00:00Z\",\"message\":\"hello\"}",
            IgnoredPaths = ["$.id", "timestamp"]
        };

        var actualWithDynamicFields = new HttpExecutionResult(
            ExecutionOutcome.Passed,
            200,
            "OK",
            [new HeaderEntry("Content-Type", "application/json")],
            "{\"id\":\"9999\",\"timestamp\":\"2026-11-07T22:00:00Z\",\"message\":\"hello\"}",
            70,
            false,
            null,
            TimeSpan.FromMilliseconds(30),
            null);

        var comparison = BaselineEngine.Compare(baseline, actualWithDynamicFields);
        Assert.True(comparison.Matched, $"Expected baseline match with ignored fields, but got diffs: {string.Join(", ", comparison.Differences.Select(d => d.Message))}");

        // Now test mismatch in unignored property
        var actualMismatch = new HttpExecutionResult(
            ExecutionOutcome.Passed,
            200,
            "OK",
            [new HeaderEntry("Content-Type", "application/json")],
            "{\"id\":\"9999\",\"timestamp\":\"2026-11-07T22:00:00Z\",\"message\":\"goodbye\"}",
            72,
            false,
            null,
            TimeSpan.FromMilliseconds(30),
            null);

        var mismatchComp = BaselineEngine.Compare(baseline, actualMismatch);
        Assert.False(mismatchComp.Matched);
        Assert.Contains(mismatchComp.Differences, d => d.Aspect == "$.message");
    }

    [Fact]
    public void Reporters_GenerateSanitizedOutputs_ForAllFormats()
    {
        var report = new CollectionExecutionReport(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Checkout Regression Suite",
            RunStatus.Passed,
            DateTimeOffset.UtcNow.AddSeconds(-5),
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(5),
            [
                new IterationExecutionRecord(
                    1,
                    RunStatus.Passed,
                    TimeSpan.FromSeconds(4),
                    [
                        new RequestExecutionRecord(
                            Guid.NewGuid(),
                            "Authenticate User",
                            1,
                            1,
                            ExecutionOutcome.Passed,
                            200,
                            TimeSpan.FromMilliseconds(150),
                            null,
                            "{\"token\":\"super_secret_token_12345\"}",
                            null,
                            [new AssertionResult(Guid.NewGuid(), "Status 200", true, "200", "200", null)])
                    ],
                    new Dictionary<string, string> { ["token"] = "super_secret_token_12345" })
            ],
            [],
            [],
            1,
            1,
            0,
            OsArchitecture: "Windows (X64)");

        var knownSecrets = new[] { "super_secret_token_12345" };

        // 1. Console Reporter
        var consoleOutput = new ConsoleReporter().GenerateReport(report, knownSecrets);
        Assert.Contains("Checkout Regression Suite", consoleOutput);
        Assert.DoesNotContain("super_secret_token_12345", consoleOutput);
        Assert.Contains("[REDACTED]", consoleOutput);

        // 2. JSON Reporter
        var jsonOutput = new JsonReporter().GenerateReport(report, knownSecrets);
        Assert.Contains("\"collectionName\": \"Checkout Regression Suite\"", jsonOutput);
        Assert.DoesNotContain("super_secret_token_12345", jsonOutput);
        Assert.Contains("[REDACTED]", jsonOutput);

        // 3. JUnit Reporter
        var junitOutput = new JUnitReporter().GenerateReport(report, knownSecrets);
        Assert.Contains("<testsuites name=\"Checkout Regression Suite\"", junitOutput);
        Assert.Contains("<testcase classname=\"Checkout Regression Suite\" name=\"Authenticate User\"", junitOutput);
        Assert.DoesNotContain("super_secret_token_12345", junitOutput);

        // 4. Markdown Reporter
        var mdOutput = new MarkdownReporter().GenerateReport(report, knownSecrets);
        Assert.Contains("# Collection Run Report: Checkout Regression Suite", mdOutput);
        Assert.Contains("| Iter 1 | Authenticate User | 200 | Passed |", mdOutput);
        Assert.DoesNotContain("super_secret_token_12345", mdOutput);

        // 5. HTML Reporter
        var htmlOutput = new HtmlReporter().GenerateReport(report, knownSecrets);
        Assert.Contains("<h1>Collection Report: Checkout Regression Suite", htmlOutput);
        Assert.Contains("Authenticate User", htmlOutput);
        Assert.DoesNotContain("super_secret_token_12345", htmlOutput);
    }
}
