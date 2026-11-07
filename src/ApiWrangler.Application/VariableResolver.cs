using System.Text.RegularExpressions;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed class VariableContext
{
    public Dictionary<string, string> RequestLocal { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> IterationData { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> RunOverrides { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LocalEnvironment { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Collection { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ProjectDefaults { get; init; } = new(StringComparer.Ordinal);
    public ISecretStore? SecretStore { get; init; }
    public Dictionary<string, string> DirectSecrets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public DynamicValueContext DynamicContext { get; init; } = new();

    public VariableContext Clone() => new()
    {
        RequestLocal = new(RequestLocal, StringComparer.Ordinal),
        IterationData = new(IterationData, StringComparer.Ordinal),
        RunOverrides = new(RunOverrides, StringComparer.Ordinal),
        LocalEnvironment = new(LocalEnvironment, StringComparer.Ordinal),
        Environment = new(Environment, StringComparer.Ordinal),
        Collection = new(Collection, StringComparer.Ordinal),
        ProjectDefaults = new(ProjectDefaults, StringComparer.Ordinal),
        SecretStore = SecretStore,
        DirectSecrets = new(DirectSecrets, StringComparer.OrdinalIgnoreCase),
        DynamicContext = DynamicContext with { }
    };
}

public sealed partial class VariableResolver
{
    private const int MaxNestingDepth = 10;
    private static readonly Regex VariableRegex = new(@"\{\{([^{}]+)\}\}", RegexOptions.Compiled);

    public async Task<string> ResolveAsync(string template, VariableContext context, string location, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains("{{", StringComparison.Ordinal))
            return template;

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new List<string>();
        var random = new Random(context.DynamicContext.Seed);

        return await ResolveInternalAsync(template, context, location, visited, stack, random, 0, token).ConfigureAwait(false);
    }

    private async Task<string> ResolveInternalAsync(
        string currentText,
        VariableContext context,
        string location,
        HashSet<string> visited,
        List<string> stack,
        Random random,
        int depth,
        CancellationToken token)
    {
        if (depth > MaxNestingDepth)
            throw new VariableResolutionException(stack.LastOrDefault() ?? "unknown", location, $"Nesting depth exceeded maximum allowed limit ({MaxNestingDepth}).");

        if (!currentText.Contains("{{", StringComparison.Ordinal))
            return currentText;

        var matches = VariableRegex.Matches(currentText);
        if (matches.Count == 0) return currentText;

        var sb = new System.Text.StringBuilder();
        int lastIndex = 0;

        foreach (Match match in matches)
        {
            sb.Append(currentText.AsSpan(lastIndex, match.Index - lastIndex));
            var rawExpression = match.Groups[1].Value.Trim();

            if (stack.Contains(rawExpression, StringComparer.Ordinal))
            {
                var cycle = string.Join(" -> ", stack) + " -> " + rawExpression;
                throw new VariableResolutionException(rawExpression, location, $"Circular reference detected: {cycle}");
            }

            stack.Add(rawExpression);
            try
            {
                var (resolvedRawValue, isDynamic) = await LookupValueAsync(rawExpression, context, location, random, token).ConfigureAwait(false);

                // Nested resolution if the value itself contains variable expressions
                string finalSubValue = resolvedRawValue;
                if (!isDynamic && resolvedRawValue.Contains("{{", StringComparison.Ordinal))
                {
                    finalSubValue = await ResolveInternalAsync(resolvedRawValue, context, location, visited, stack, random, depth + 1, token).ConfigureAwait(false);
                }

                sb.Append(finalSubValue);
            }
            finally
            {
                stack.RemoveAt(stack.Count - 1);
            }

            lastIndex = match.Index + match.Length;
        }

        sb.Append(currentText.AsSpan(lastIndex));
        return sb.ToString();
    }

    private static async Task<(string Value, bool IsDynamic)> LookupValueAsync(
        string expression,
        VariableContext context,
        string location,
        Random random,
        CancellationToken token)
    {
        // 1. Secret namespace: {{secret:name}}
        if (expression.StartsWith("secret:", StringComparison.OrdinalIgnoreCase))
        {
            var secretKey = expression["secret:".Length..].Trim();
            if (context.DirectSecrets.TryGetValue(secretKey, out var directSec))
                return (directSec, false);

            var hostEnvVal = Environment.GetEnvironmentVariable($"API_WRANGLER_SECRET_{secretKey}") ??
                             Environment.GetEnvironmentVariable(secretKey);
            if (hostEnvVal is not null)
                return (hostEnvVal, false);

            if (context.SecretStore is not null)
            {
                var storeVal = await context.SecretStore.GetSecretAsync(secretKey, token).ConfigureAwait(false);
                if (storeVal is not null) return (storeVal, false);
            }

            throw new VariableResolutionException(expression, location, $"Secret '{secretKey}' not found in secret store or host environment.");
        }

        // 2. Dynamic helpers: {{$uuid}}, {{$guid}}, {{$timestamp}}, {{$isoTimestamp}}, {{$randomInt}}, {{$randomString}}, {{$runId}}, {{$iteration}}
        if (expression.StartsWith("$", StringComparison.Ordinal))
        {
            var dynKey = expression.ToLowerInvariant();
            switch (dynKey)
            {
                case "$uuid":
                case "$guid":
                    var guidBytes = new byte[16];
                    random.NextBytes(guidBytes);
                    return (new Guid(guidBytes).ToString(), true);

                case "$timestamp":
                    return (context.DynamicContext.Timestamp.ToUnixTimeSeconds().ToString(), true);

                case "$isotimestamp":
                    return (context.DynamicContext.Timestamp.ToString("O"), true);

                case "$randomint":
                    return (random.Next(0, 1_000_000).ToString(), true);

                case "$randomstring":
                    const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
                    var strChars = new char[12];
                    for (int i = 0; i < strChars.Length; i++) strChars[i] = chars[random.Next(chars.Length)];
                    return (new string(strChars), true);

                case "$runid":
                    return (context.DynamicContext.RunId, true);

                case "$iteration":
                    return (context.DynamicContext.IterationIndex.ToString(), true);

                default:
                    throw new VariableResolutionException(expression, location, $"Unknown dynamic helper '{expression}'.");
            }
        }

        // 3. Documented scope precedence:
        // Request-local runtime variables
        if (context.RequestLocal.TryGetValue(expression, out var reqVal))
            return (reqVal, false);

        // Current iteration data
        if (context.IterationData.TryGetValue(expression, out var iterVal))
            return (iterVal, false);

        // Run overrides
        if (context.RunOverrides.TryGetValue(expression, out var runVal))
            return (runVal, false);

        // Local environment (personal overrides)
        if (context.LocalEnvironment.TryGetValue(expression, out var localEnvVal))
            return (localEnvVal, false);

        // Selected environment
        if (context.Environment.TryGetValue(expression, out var envVal))
            return (envVal, false);

        // Collection
        if (context.Collection.TryGetValue(expression, out var colVal))
            return (colVal, false);

        // Project defaults
        if (context.ProjectDefaults.TryGetValue(expression, out var projVal))
            return (projVal, false);

        // Missing variable
        throw new VariableResolutionException(expression, location, "Variable is not defined in any active scope.");
    }

    public async Task<IReadOnlyList<ResolvedVariableInspection>> InspectAsync(
        string template,
        VariableContext context,
        CancellationToken token = default)
    {
        var result = new List<ResolvedVariableInspection>();
        if (string.IsNullOrEmpty(template) || !template.Contains("{{", StringComparison.Ordinal))
            return result;

        var matches = VariableRegex.Matches(template);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in matches)
        {
            var expr = match.Groups[1].Value.Trim();
            if (!seen.Add(expr)) continue;

            if (expr.StartsWith("secret:", StringComparison.OrdinalIgnoreCase))
            {
                var secretKey = expr["secret:".Length..].Trim();
                bool exists = context.DirectSecrets.ContainsKey(secretKey) ||
                              Environment.GetEnvironmentVariable($"API_WRANGLER_SECRET_{secretKey}") is not null ||
                              Environment.GetEnvironmentVariable(secretKey) is not null ||
                              (context.SecretStore is not null && (await context.SecretStore.GetSecretAsync(secretKey, token).ConfigureAwait(false)) is not null);

                result.Add(new ResolvedVariableInspection(
                    expr,
                    exists ? "[REDACTED]" : "(unresolved)",
                    VariableSource.Secret,
                    IsSecret: true));
                continue;
            }

            if (expr.StartsWith("$", StringComparison.Ordinal))
            {
                result.Add(new ResolvedVariableInspection(expr, "(dynamic helper)", VariableSource.Dynamic, IsSecret: false));
                continue;
            }

            if (context.RequestLocal.TryGetValue(expr, out var v1))
                result.Add(new ResolvedVariableInspection(expr, v1, VariableSource.RequestLocal, false));
            else if (context.IterationData.TryGetValue(expr, out var v2))
                result.Add(new ResolvedVariableInspection(expr, v2, VariableSource.Dataset, false));
            else if (context.RunOverrides.TryGetValue(expr, out var v3))
                result.Add(new ResolvedVariableInspection(expr, v3, VariableSource.RunOverride, false));
            else if (context.LocalEnvironment.TryGetValue(expr, out var v4))
                result.Add(new ResolvedVariableInspection(expr, v4, VariableSource.LocalEnvironment, false));
            else if (context.Environment.TryGetValue(expr, out var v5))
                result.Add(new ResolvedVariableInspection(expr, v5, VariableSource.Environment, false));
            else if (context.Collection.TryGetValue(expr, out var v6))
                result.Add(new ResolvedVariableInspection(expr, v6, VariableSource.Collection, false));
            else if (context.ProjectDefaults.TryGetValue(expr, out var v7))
                result.Add(new ResolvedVariableInspection(expr, v7, VariableSource.ProjectDefault, false));
            else
                result.Add(new ResolvedVariableInspection(expr, "(unresolved)", VariableSource.ProjectDefault, false));
        }

        return result;
    }
}

