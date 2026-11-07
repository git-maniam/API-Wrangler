using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public static class AssertionEngine
{
    public static IReadOnlyList<AssertionResult> EvaluateAll(
        IReadOnlyList<RequestAssertionDefinition>? assertions,
        HttpExecutionResult result)
    {
        if (assertions is null || assertions.Count == 0)
        {
            return [];
        }

        var results = new List<AssertionResult>(assertions.Count);
        foreach (var assertion in assertions)
        {
            results.Add(Evaluate(assertion, result));
        }

        return results;
    }

    public static AssertionResult Evaluate(
        RequestAssertionDefinition assertion,
        HttpExecutionResult result)
    {
        var id = assertion.Id;
        var expectedStr = assertion.Expected?.ToString() ?? "";
        var name = !string.IsNullOrWhiteSpace(assertion.Name)
            ? assertion.Name
            : $"{assertion.Type} {assertion.Operator} {expectedStr}".Trim();

        if (result.Outcome is not (ExecutionOutcome.Passed or ExecutionOutcome.Failed))
        {
            return new AssertionResult(
                id,
                name,
                false,
                null,
                expectedStr,
                $"Not evaluated due to request outcome: {result.Outcome}");
        }

        try
        {
            return assertion.Type.ToLowerInvariant() switch
            {
                "status" or "statuscode" => EvaluateStatus(assertion, result, id, name, expectedStr),
                "header" => EvaluateHeader(assertion, result, id, name, expectedStr),
                "cookie" => EvaluateCookie(assertion, result, id, name, expectedStr),
                "bodycontains" or "body" => EvaluateBody(assertion, result, id, name, expectedStr),
                "jsonpath" or "json" => EvaluateJsonPath(assertion, result, id, name, expectedStr),
                "responsetime" or "duration" => EvaluateResponseTime(assertion, result, id, name, expectedStr),
                "responsesize" or "size" => EvaluateResponseSize(assertion, result, id, name, expectedStr),
                "regex" => EvaluateRegex(assertion, result, id, name, expectedStr),
                _ => new AssertionResult(id, name, false, null, expectedStr, $"Unsupported assertion type: '{assertion.Type}'")
            };
        }
        catch (Exception ex)
        {
            return new AssertionResult(id, name, false, null, expectedStr, $"Assertion evaluation error: {ex.Message}");
        }
    }

    private static AssertionResult EvaluateStatus(
        RequestAssertionDefinition a,
        HttpExecutionResult r,
        Guid id,
        string name,
        string expectedStr)
    {
        var actual = r.StatusCode?.ToString();
        var (passed, message) = Compare(actual, a.Operator, expectedStr);
        return new AssertionResult(id, name, passed, actual, expectedStr, message);
    }

    private static AssertionResult EvaluateHeader(
        RequestAssertionDefinition a,
        HttpExecutionResult r,
        Guid id,
        string name,
        string expectedStr)
    {
        var target = a.Target ?? "";
        var header = r.Headers.FirstOrDefault(h => string.Equals(h.Name, target, StringComparison.OrdinalIgnoreCase));

        if (string.Equals(a.Operator, "exists", StringComparison.OrdinalIgnoreCase))
        {
            bool passed = header is not null;
            return new AssertionResult(id, name, passed, header?.Value, "header exists", passed ? null : $"Header '{target}' was not found.");
        }

        if (string.Equals(a.Operator, "notexists", StringComparison.OrdinalIgnoreCase))
        {
            bool passed = header is null;
            return new AssertionResult(id, name, passed, header?.Value, "header not exists", passed ? null : $"Header '{target}' was found unexpectedly.");
        }

        if (header is null)
        {
            return new AssertionResult(id, name, false, null, expectedStr, $"Header '{target}' was not found.");
        }

        var (p, msg) = Compare(header.Value, a.Operator, expectedStr);
        return new AssertionResult(id, name, p, header.Value, expectedStr, msg);
    }

    private static AssertionResult EvaluateCookie(
        RequestAssertionDefinition a,
        HttpExecutionResult r,
        Guid id,
        string name,
        string expectedStr)
    {
        var target = a.Target ?? "";
        var cookie = r.Cookies?.FirstOrDefault(c => string.Equals(c.Name, target, StringComparison.OrdinalIgnoreCase));

        if (string.Equals(a.Operator, "exists", StringComparison.OrdinalIgnoreCase))
        {
            bool passed = cookie is not null;
            return new AssertionResult(id, name, passed, cookie?.Value, "cookie exists", passed ? null : $"Cookie '{target}' was not found.");
        }

        if (string.Equals(a.Operator, "notexists", StringComparison.OrdinalIgnoreCase))
        {
            bool passed = cookie is null;
            return new AssertionResult(id, name, passed, cookie?.Value, "cookie not exists", passed ? null : $"Cookie '{target}' was found unexpectedly.");
        }

        if (cookie is null)
        {
            return new AssertionResult(id, name, false, null, expectedStr, $"Cookie '{target}' was not found.");
        }

        var (p, msg) = Compare(cookie.Value, a.Operator, expectedStr);
        return new AssertionResult(id, name, p, cookie.Value, expectedStr, msg);
    }

    private static AssertionResult EvaluateBody(
        RequestAssertionDefinition a,
        HttpExecutionResult r,
        Guid id,
        string name,
        string expectedStr)
    {
        var body = r.Preview;
        var op = string.Equals(a.Type, "bodycontains", StringComparison.OrdinalIgnoreCase) && string.Equals(a.Operator, "equals", StringComparison.OrdinalIgnoreCase)
            ? "contains"
            : a.Operator;

        var (passed, message) = Compare(body, op, expectedStr);
        return new AssertionResult(id, name, passed, body.Length > 200 ? body[..200] + "..." : body, expectedStr, message);
    }

    private static AssertionResult EvaluateJsonPath(
        RequestAssertionDefinition a,
        HttpExecutionResult r,
        Guid id,
        string name,
        string expectedStr)
    {
        if (string.IsNullOrWhiteSpace(r.Preview))
        {
            return new AssertionResult(id, name, false, null, expectedStr, "Response body was empty.");
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(r.Preview);
        }
        catch (Exception ex)
        {
            return new AssertionResult(id, name, false, null, expectedStr, $"Invalid JSON response: {ex.Message}");
        }

        var target = a.Target ?? "";
        var node = ResolvePath(root, target);

        if (string.Equals(a.Operator, "exists", StringComparison.OrdinalIgnoreCase))
        {
            bool passed = node is not null;
            return new AssertionResult(id, name, passed, node?.ToJsonString(), "path exists", passed ? null : $"JSON path '{target}' was not found.");
        }

        if (string.Equals(a.Operator, "notexists", StringComparison.OrdinalIgnoreCase))
        {
            bool passed = node is null;
            return new AssertionResult(id, name, passed, node?.ToJsonString(), "path not exists", passed ? null : $"JSON path '{target}' was found unexpectedly.");
        }

        if (node is null)
        {
            return new AssertionResult(id, name, false, null, expectedStr, $"JSON path '{target}' resolved to null or was missing.");
        }

        var actualVal = node is JsonValue jv ? jv.ToString() : node.ToJsonString();
        var (p, msg) = Compare(actualVal, a.Operator, expectedStr);
        return new AssertionResult(id, name, p, actualVal, expectedStr, msg);
    }

    private static AssertionResult EvaluateResponseTime(
        RequestAssertionDefinition a,
        HttpExecutionResult r,
        Guid id,
        string name,
        string expectedStr)
    {
        var actual = ((long)r.Duration.TotalMilliseconds).ToString();
        var op = string.IsNullOrWhiteSpace(a.Operator) || string.Equals(a.Operator, "equals", StringComparison.OrdinalIgnoreCase) ? "lessThan" : a.Operator;
        var (passed, message) = Compare(actual, op, expectedStr);
        return new AssertionResult(id, name, passed, $"{actual} ms", $"{expectedStr} ms", message);
    }

    private static AssertionResult EvaluateResponseSize(
        RequestAssertionDefinition a,
        HttpExecutionResult r,
        Guid id,
        string name,
        string expectedStr)
    {
        var actual = r.BytesReceived.ToString();
        var (passed, message) = Compare(actual, a.Operator, expectedStr);
        return new AssertionResult(id, name, passed, $"{actual} B", $"{expectedStr} B", message);
    }

    private static AssertionResult EvaluateRegex(
        RequestAssertionDefinition a,
        HttpExecutionResult r,
        Guid id,
        string name,
        string expectedStr)
    {
        var input = r.Preview;
        bool match = Regex.IsMatch(input, expectedStr);
        bool notMatchOp = string.Equals(a.Operator, "notmatches", StringComparison.OrdinalIgnoreCase) || string.Equals(a.Operator, "notMatches", StringComparison.OrdinalIgnoreCase);
        bool passed = notMatchOp ? !match : match;
        return new AssertionResult(
            id,
            name,
            passed,
            input.Length > 100 ? input[..100] + "..." : input,
            expectedStr,
            passed ? null : $"Pattern '{expectedStr}' {(notMatchOp ? "matched unexpectedly" : "did not match")}.");
    }

    private static (bool Passed, string? Message) Compare(string? actual, string op, string expected)
    {
        actual ??= "";
        var normalizedOp = op.ToLowerInvariant();

        switch (normalizedOp)
        {
            case "equals" or "==" or "eq":
                if (double.TryParse(actual, out var aNum) && double.TryParse(expected, out var eNum))
                {
                    bool numMatch = Math.Abs(aNum - eNum) < 0.000001;
                    return (numMatch, numMatch ? null : $"Expected '{expected}' but got '{actual}'.");
                }
                bool match = string.Equals(actual, expected, StringComparison.Ordinal);
                return (match, match ? null : $"Expected '{expected}' but got '{actual}'.");

            case "notequals" or "!=" or "neq":
                if (double.TryParse(actual, out var aNumN) && double.TryParse(expected, out var eNumN))
                {
                    bool numDiff = Math.Abs(aNumN - eNumN) >= 0.000001;
                    return (numDiff, numDiff ? null : $"Expected value not to equal '{expected}'.");
                }
                bool diff = !string.Equals(actual, expected, StringComparison.Ordinal);
                return (diff, diff ? null : $"Expected value not to equal '{expected}'.");

            case "contains":
                bool cont = actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
                return (cont, cont ? null : $"Expected string to contain '{expected}'.");

            case "notcontains":
                bool notCont = !actual.Contains(expected, StringComparison.OrdinalIgnoreCase);
                return (notCont, notCont ? null : $"Expected string not to contain '{expected}'.");

            case "greaterthan" or ">" or "gt":
                if (double.TryParse(actual, out var aG) && double.TryParse(expected, out var eG))
                {
                    bool g = aG > eG;
                    return (g, g ? null : $"Expected {actual} > {expected}.");
                }
                return (false, $"Cannot compare non-numeric values '{actual}' and '{expected}' for greaterThan.");

            case "lessthan" or "<" or "lt":
                if (double.TryParse(actual, out var aL) && double.TryParse(expected, out var eL))
                {
                    bool l = aL < eL;
                    return (l, l ? null : $"Expected {actual} < {expected}.");
                }
                return (false, $"Cannot compare non-numeric values '{actual}' and '{expected}' for lessThan.");

            case "greaterthanorequal" or ">=" or "gte":
                if (double.TryParse(actual, out var aGe) && double.TryParse(expected, out var eGe))
                {
                    bool ge = aGe >= eGe;
                    return (ge, ge ? null : $"Expected {actual} >= {expected}.");
                }
                return (false, $"Cannot compare non-numeric values '{actual}' and '{expected}'.");

            case "lessthanorequal" or "<=" or "lte":
                if (double.TryParse(actual, out var aLe) && double.TryParse(expected, out var eLe))
                {
                    bool le = aLe <= eLe;
                    return (le, le ? null : $"Expected {actual} <= {expected}.");
                }
                return (false, $"Cannot compare non-numeric values '{actual}' and '{expected}'.");

            case "matches":
                bool reg = Regex.IsMatch(actual, expected);
                return (reg, reg ? null : $"Expected '{actual}' to match pattern '{expected}'.");

            default:
                return (false, $"Unknown assertion operator: '{op}'.");
        }
    }

    private static JsonNode? ResolvePath(JsonNode? root, string path)
    {
        if (root is null || string.IsNullOrWhiteSpace(path) || path == "$")
        {
            return root;
        }

        var normalized = path.StartsWith("$.") ? path[2..] : (path.StartsWith("$") ? path[1..] : path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return root;
        }

        // Split tokens by '.' while respecting indexers like items[0]
        var tokens = normalized.Split('.');
        JsonNode? current = root;

        foreach (var token in tokens)
        {
            if (current is null) return null;

            var prop = token;
            int? index = null;

            var bracketStart = token.IndexOf('[');
            if (bracketStart >= 0 && token.EndsWith(']'))
            {
                prop = token[..bracketStart];
                var indexStr = token[(bracketStart + 1)..^1];
                if (int.TryParse(indexStr, out var parsedIndex))
                {
                    index = parsedIndex;
                }
            }

            if (!string.IsNullOrEmpty(prop))
            {
                if (current is JsonObject obj && obj.TryGetPropertyValue(prop, out var child))
                {
                    current = child;
                }
                else
                {
                    return null;
                }
            }

            if (index.HasValue)
            {
                if (current is JsonArray arr && index.Value >= 0 && index.Value < arr.Count)
                {
                    current = arr[index.Value];
                }
                else
                {
                    return null;
                }
            }
        }

        return current;
    }
}
