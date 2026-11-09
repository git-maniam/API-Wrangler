using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ApiWrangler.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApiWrangler.Application;

public sealed class MockEngine : IAsyncDisposable
{
    private readonly List<MockRule> rules = new();
    private readonly List<MockServerLogEntry> logs = new();
    private readonly Dictionary<string, string> scenarioStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object syncRoot = new();
    private WebApplication? app;
    private Random? seededRandom;
    private int intermittentSeed;
    private string? currentListeningUrl;
    private MockServerConfiguration? activeConfig;

    public string? ListeningUrl => currentListeningUrl;
    public IReadOnlyList<MockRule> Rules { get { lock (syncRoot) return rules.ToList(); } }
    public IReadOnlyList<MockServerLogEntry> Logs { get { lock (syncRoot) return logs.ToList(); } }
    public IReadOnlyDictionary<string, string> ScenarioStates { get { lock (syncRoot) return new Dictionary<string, string>(scenarioStates); } }

    public MockEngine(int? defaultSeed = null)
    {
        intermittentSeed = defaultSeed ?? 42;
        seededRandom = new Random(intermittentSeed);
    }

    public void AddRule(MockRule rule)
    {
        lock (syncRoot)
        {
            rules.Add(rule);
        }
    }

    public void ClearRules()
    {
        lock (syncRoot)
        {
            rules.Clear();
        }
    }

    public void ResetState()
    {
        lock (syncRoot)
        {
            scenarioStates.Clear();
            logs.Clear();
            seededRandom = new Random(intermittentSeed);
        }
    }

    public void SetScenarioState(string scenario, string state)
    {
        lock (syncRoot)
        {
            scenarioStates[scenario] = state;
        }
    }

    public MockMatchResult MatchRequest(
        string method,
        string path,
        IQueryCollection? query = null,
        IHeaderDictionary? headers = null,
        string? body = null)
    {
        lock (syncRoot)
        {
            var scoredCandidates = new List<(MockRule Rule, int Specificity)>();

            foreach (var rule in rules)
            {
                if (!IsMethodMatch(rule.Method, method)) continue;
                if (!IsPathMatch(rule.PathPattern, path, out int pathScore)) continue;

                if (!IsQueryParamsMatch(rule.QueryParams, query, out int queryScore)) continue;
                if (!IsHeadersMatch(rule.Headers, headers, out int headerScore)) continue;
                if (!IsBodyMatch(rule.BodyPattern, body, out int bodyScore)) continue;

                // Scenario state check
                if (!string.IsNullOrEmpty(rule.ScenarioStateRequired))
                {
                    var parts = rule.ScenarioStateRequired.Split('=', 2);
                    string scName = parts[0].Trim();
                    string reqState = parts.Length > 1 ? parts[1].Trim() : "Initial";
                    string curState = scenarioStates.TryGetValue(scName, out var s) ? s : "Initial";
                    if (!curState.Equals(reqState, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                int totalSpecificity = (rule.Priority * 1000) + pathScore + queryScore + headerScore + bodyScore;
                scoredCandidates.Add((rule, totalSpecificity));
            }

            if (scoredCandidates.Count == 0)
            {
                return new MockMatchResult(
                    IsMatched: false,
                    Rule: null,
                    SpecificityScore: 0,
                    MatchExplanations: [$"No rules matched request {method} {path}."]
                );
            }

            // Order by Specificity descending
            scoredCandidates.Sort((a, b) => b.Specificity.CompareTo(a.Specificity));

            int maxScore = scoredCandidates[0].Specificity;
            var topScored = scoredCandidates.Where(c => c.Specificity == maxScore).ToList();

            if (topScored.Count > 1)
            {
                // Ambiguous match
                return new MockMatchResult(
                    IsMatched: true,
                    Rule: topScored[0].Rule,
                    SpecificityScore: maxScore,
                    MatchExplanations: [$"Ambiguous match: {topScored.Count} rules scored equally with specificity {maxScore}."],
                    IsAmbiguous: true,
                    AmbiguousCandidates: topScored.Select(c => c.Rule).ToList()
                );
            }

            var best = topScored[0];
            return new MockMatchResult(
                IsMatched: true,
                Rule: best.Rule,
                SpecificityScore: best.Specificity,
                MatchExplanations: [$"Rule '{best.Rule.Name}' matched with specificity {best.Specificity}."]
            );
        }
    }

    public HostedMockDeploymentStatus GetDeploymentStatus()
    {
        lock (syncRoot)
        {
            return new HostedMockDeploymentStatus(
                IsListening: app is not null,
                ListeningUrl: currentListeningUrl,
                ActiveRulesCount: rules.Count,
                RecordedLogCount: logs.Count,
                ExternalBindingEnabled: activeConfig?.AllowExternalBinding ?? false,
                AuthenticationEnforced: activeConfig?.RequireAuthentication == true || !string.IsNullOrWhiteSpace(activeConfig?.AuthToken)
            );
        }
    }

    public async Task StartAsync(MockServerConfiguration? config = null, CancellationToken token = default)
    {
        config ??= new MockServerConfiguration();
        activeConfig = config;

        if (config.AllowExternalBinding && string.IsNullOrWhiteSpace(config.AuthToken))
        {
            throw new InvalidOperationException("Remote exposure requires explicit authentication configuration (AuthToken). Starting a mock server with external binding without authentication is not permitted.");
        }

        if (config.RequireAuthentication && string.IsNullOrWhiteSpace(config.AuthToken))
        {
            throw new InvalidOperationException("RequireAuthentication is enabled but no AuthToken was configured.");
        }

        static bool IsLoopbackHost(string h) =>
            string.Equals(h, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(h, "::1", StringComparison.OrdinalIgnoreCase);

        if (!config.AllowExternalBinding && !IsLoopbackHost(config.Host))
        {
            throw new InvalidOperationException($"Cannot bind to non-loopback host '{config.Host}' without explicit opt-in (AllowExternalBinding). Enable external binding and configure an authentication token to expose the mock server.");
        }

        string host = config.Host;
        string url = $"http://{host}:{config.Port}";

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);

        app = builder.Build();

        // Admin endpoints
        app.MapGet("/__admin/health", () =>
        {
            return Results.Ok(GetDeploymentStatus());
        });

        app.MapGet("/__admin/rules", (HttpContext context) =>
        {
            if (!IsAdminAuthorized(context, config))
            {
                return Results.Json(new { error = "Unauthorized", message = "Admin authorization required." }, statusCode: 401);
            }
            return Results.Ok(Rules);
        });

        app.MapGet("/__admin/logs", (HttpContext context) =>
        {
            if (!IsAdminAuthorized(context, config))
            {
                return Results.Json(new { error = "Unauthorized", message = "Admin authorization required." }, statusCode: 401);
            }
            return Results.Ok(Logs);
        });

        app.MapPost("/__admin/state/reset", (HttpContext context) =>
        {
            if (!IsAdminAuthorized(context, config))
            {
                return Results.Json(new { error = "Unauthorized", message = "Admin authorization required." }, statusCode: 401);
            }
            ResetState();
            return Results.Ok(new { status = "ResetSuccessful" });
        });

        app.MapFallback(async (HttpContext context) =>
        {
            var sw = Stopwatch.StartNew();
            string method = context.Request.Method;
            string path = context.Request.Path.Value ?? "/";
            string query = context.Request.QueryString.Value ?? "";

            // Check authentication for mock routes if configured
            bool requiresAuth = config.RequireAuthentication || !string.IsNullOrWhiteSpace(config.AuthToken);
            if (requiresAuth && !IsAuthorized(context, config.AuthToken!))
            {
                sw.Stop();
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\": \"Unauthorized\", \"message\": \"Valid authorization header or API key required.\"}", context.RequestAborted).ConfigureAwait(false);

                var unauthLog = new MockServerLogEntry(
                    Timestamp: DateTimeOffset.UtcNow,
                    Method: method,
                    Path: path,
                    QueryString: query,
                    Headers: context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()),
                    Body: null,
                    MatchedRuleCount: 0,
                    MatchedRuleId: null,
                    ResponseStatusCode: 401,
                    FaultApplied: "AuthenticationRequired",
                    Duration: sw.Elapsed
                );
                lock (syncRoot)
                {
                    logs.Add(unauthLog);
                    int maxRetention = config.MaxLogRetention > 0 ? config.MaxLogRetention : 1000;
                    while (logs.Count > maxRetention) logs.RemoveAt(0);
                }
                return;
            }

            string? body = null;
            if (context.Request.ContentLength > 0 || (context.Request.ContentType?.Contains("text") == true || context.Request.ContentType?.Contains("json") == true))
            {
                using var reader = new StreamReader(context.Request.Body);
                body = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
            }

            var match = MatchRequest(method, path, context.Request.Query, context.Request.Headers, body);

            string? faultApplied = null;
            int responseStatus = 200;

            if (!match.IsMatched || match.Rule is null)
            {
                responseStatus = 404;
                context.Response.StatusCode = 404;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($"{{\"error\": \"Not Found\", \"explanation\": \"{match.MatchExplanations[0]}\"}}", context.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                var rule = match.Rule;

                // Handle state transition
                if (!string.IsNullOrEmpty(rule.ScenarioStateTransition))
                {
                    var parts = rule.ScenarioStateTransition.Split('=', 2);
                    string scName = parts[0].Trim();
                    string newState = parts.Length > 1 ? parts[1].Trim() : "Complete";
                    SetScenarioState(scName, newState);
                }

                // Check intermittent failure
                bool isIntermittentFail = false;
                if (rule.IntermittentFailureRate > 0)
                {
                    lock (syncRoot)
                    {
                        var rand = rule.IntermittentSeed.HasValue ? new Random(rule.IntermittentSeed.Value) : seededRandom!;
                        if (rand.NextDouble() < rule.IntermittentFailureRate)
                        {
                            isIntermittentFail = true;
                        }
                    }
                }

                if (isIntermittentFail)
                {
                    faultApplied = "IntermittentFailure (503)";
                    responseStatus = 503;
                    context.Response.StatusCode = 503;
                    await context.Response.WriteAsync("{\"error\": \"Intermittent fault triggered.\"}", context.RequestAborted).ConfigureAwait(false);
                }
                else
                {
                    // Fault simulation
                    switch (rule.FaultType)
                    {
                        case MockFaultType.DelayedResponse:
                            faultApplied = "DelayedResponse";
                            await Task.Delay(rule.ResponseDelay > TimeSpan.Zero ? rule.ResponseDelay : TimeSpan.FromMilliseconds(500), context.RequestAborted).ConfigureAwait(false);
                            break;
                        case MockFaultType.HttpError:
                            faultApplied = "HttpError";
                            responseStatus = rule.ResponseStatusCode >= 400 ? rule.ResponseStatusCode : 500;
                            context.Response.StatusCode = responseStatus;
                            await context.Response.WriteAsync(rule.ResponseBody ?? "{\"error\": \"Simulated HTTP fault.\"}", context.RequestAborted).ConfigureAwait(false);
                            break;
                        case MockFaultType.MalformedPayload:
                            faultApplied = "MalformedPayload";
                            responseStatus = 200;
                            context.Response.StatusCode = 200;
                            context.Response.ContentType = "application/json";
                            await context.Response.WriteAsync("{ malformed json, unquoted: 123 ", context.RequestAborted).ConfigureAwait(false);
                            break;
                        case MockFaultType.RateLimit:
                            faultApplied = "RateLimit (429)";
                            responseStatus = 429;
                            context.Response.StatusCode = 429;
                            context.Response.Headers.RetryAfter = "30";
                            await context.Response.WriteAsync("{\"error\": \"Too Many Requests\"}", context.RequestAborted).ConfigureAwait(false);
                            break;
                        case MockFaultType.ConnectionTermination:
                            faultApplied = "ConnectionTermination";
                            context.Abort();
                            return;
                        default:
                            if (rule.ResponseDelay > TimeSpan.Zero)
                            {
                                await Task.Delay(rule.ResponseDelay, context.RequestAborted).ConfigureAwait(false);
                            }
                            responseStatus = rule.ResponseStatusCode;
                            context.Response.StatusCode = rule.ResponseStatusCode;
                            if (rule.ResponseHeaders is not null)
                            {
                                foreach (var (k, v) in rule.ResponseHeaders)
                                {
                                    context.Response.Headers[k] = v;
                                }
                            }
                            if (!string.IsNullOrEmpty(rule.ResponseBody))
                            {
                                if (string.IsNullOrEmpty(context.Response.ContentType))
                                {
                                    context.Response.ContentType = "application/json";
                                }
                                await context.Response.WriteAsync(rule.ResponseBody, context.RequestAborted).ConfigureAwait(false);
                            }
                            break;
                    }
                }
            }

            sw.Stop();
            var logHeaders = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());
            var logEntry = new MockServerLogEntry(
                Timestamp: DateTimeOffset.UtcNow,
                Method: method,
                Path: path,
                QueryString: query,
                Headers: logHeaders,
                Body: body,
                MatchedRuleCount: match.IsMatched ? (match.IsAmbiguous ? match.AmbiguousCandidates?.Count ?? 1 : 1) : 0,
                MatchedRuleId: match.Rule?.Id,
                ResponseStatusCode: responseStatus,
                FaultApplied: faultApplied,
                Duration: sw.Elapsed
            );

            lock (syncRoot)
            {
                logs.Add(logEntry);
                int maxRetention = config.MaxLogRetention > 0 ? config.MaxLogRetention : 1000;
                while (logs.Count > maxRetention) logs.RemoveAt(0);
            }
        });

        await app.StartAsync(token).ConfigureAwait(false);
        currentListeningUrl = app.Urls.FirstOrDefault();
    }

    public async Task StopAsync(CancellationToken token = default)
    {
        if (app is not null)
        {
            await app.StopAsync(token).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            app = null;
            currentListeningUrl = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    // Matching Helpers
    private static bool IsMethodMatch(string ruleMethod, string requestMethod)
    {
        if (string.IsNullOrEmpty(ruleMethod) || ruleMethod == "*") return true;
        return string.Equals(ruleMethod, requestMethod, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPathMatch(string pattern, string path, out int specificity)
    {
        specificity = 0;
        if (string.IsNullOrEmpty(pattern) || pattern == "*")
        {
            specificity = 1;
            return true;
        }

        // Exact match
        if (string.Equals(pattern, path, StringComparison.OrdinalIgnoreCase))
        {
            specificity = 100 + pattern.Length;
            return true;
        }

        // Wildcard match e.g. /users/*
        if (pattern.EndsWith("/*"))
        {
            string prefix = pattern[..^2];
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                specificity = 50 + prefix.Length;
                return true;
            }
        }

        // Parameterized match e.g. /users/{id}
        if (pattern.Contains('{') && pattern.Contains('}'))
        {
            string regexPattern = "^" + Regex.Replace(pattern, @"\{[a-zA-Z0-9_]+\}", "([^/]+)") + "$";
            if (Regex.IsMatch(path, regexPattern, RegexOptions.IgnoreCase))
            {
                specificity = 60 + pattern.Length;
                return true;
            }
        }

        return false;
    }

    private static bool IsQueryParamsMatch(IReadOnlyDictionary<string, string>? ruleQuery, IQueryCollection? reqQuery, out int specificity)
    {
        specificity = 0;
        if (ruleQuery is null || ruleQuery.Count == 0) return true;
        if (reqQuery is null) return false;

        foreach (var (k, v) in ruleQuery)
        {
            if (!reqQuery.TryGetValue(k, out var reqVal)) return false;
            if (v != "*" && !string.Equals(v, reqVal.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
            specificity += 20;
        }

        return true;
    }

    private static bool IsHeadersMatch(IReadOnlyDictionary<string, string>? ruleHeaders, IHeaderDictionary? reqHeaders, out int specificity)
    {
        specificity = 0;
        if (ruleHeaders is null || ruleHeaders.Count == 0) return true;
        if (reqHeaders is null) return false;

        foreach (var (k, v) in ruleHeaders)
        {
            if (!reqHeaders.TryGetValue(k, out var reqVal)) return false;
            if (v != "*" && !string.Equals(v, reqVal.ToString(), StringComparison.OrdinalIgnoreCase)) return false;
            specificity += 15;
        }

        return true;
    }

    private static bool IsBodyMatch(string? pattern, string? body, out int specificity)
    {
        specificity = 0;
        if (string.IsNullOrEmpty(pattern)) return true;
        if (string.IsNullOrEmpty(body)) return false;

        if (body.Contains(pattern, StringComparison.OrdinalIgnoreCase))
        {
            specificity = 30;
            return true;
        }

        try
        {
            if (Regex.IsMatch(body, pattern, RegexOptions.IgnoreCase))
            {
                specificity = 35;
                return true;
            }
        }
        catch { }

        return false;
    }

    private static bool IsAuthorized(HttpContext context, string expectedToken)
    {
        if (context.Request.Headers.TryGetValue("Authorization", out var authValues))
        {
            string authHeader = authValues.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                string token = authHeader["Bearer ".Length..].Trim();
                if (FixedTimeEquals(token, expectedToken)) return true;
            }
        }

        if (context.Request.Headers.TryGetValue("X-Api-Key", out var apiKeyValues))
        {
            string key = apiKeyValues.ToString().Trim();
            if (FixedTimeEquals(key, expectedToken)) return true;
        }

        return false;
    }

    private static bool IsAdminAuthorized(HttpContext context, MockServerConfiguration config)
    {
        // If no admin key and no auth token and auth not required, open by default (e.g. local loopback)
        if (string.IsNullOrWhiteSpace(config.AdminApiKey) && string.IsNullOrWhiteSpace(config.AuthToken) && !config.RequireAuthentication)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(config.AdminApiKey) && IsAuthorized(context, config.AdminApiKey))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(config.AuthToken) && IsAuthorized(context, config.AuthToken))
        {
            return true;
        }

        return false;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        byte[] aBytes = Encoding.UTF8.GetBytes(a);
        byte[] bBytes = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}

