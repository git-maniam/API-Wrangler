using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public static class AiContextSanitizer
{
    private static readonly Regex GitPersonalAccessTokenRegex = new(@"\b(ghp_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{50,}|gho_[A-Za-z0-9]{36,}|ghu_[A-Za-z0-9]{36,}|ghs_[A-Za-z0-9]{36,}|ghr_[A-Za-z0-9]{36,})\b", RegexOptions.Compiled);
    private static readonly Regex GitRemoteCredentialRegex = new(@"https://(?<user>[^:\s/]+):(?<pass>[^@\s/]+)@(?<host>[A-Za-z0-9.-]+)", RegexOptions.Compiled);
    private static readonly Regex PrivateKeyRegex = new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]+?-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled);
    private static readonly Regex BearerOrBasicAuthRegex = new(@"(?i)\b(Bearer|Basic)\s+([A-Za-z0-9_\-\.\+/=]{16,})", RegexOptions.Compiled);
    private static readonly Regex GenericApiKeyRegex = new(@"(?i)(api[_-]?key|secret|token|password|passwd|auth)\s*[:=]\s*[""']?([A-Za-z0-9_\-\.]{8,})[""']?", RegexOptions.Compiled);

    public static AiSanitizedContext Sanitize(IEnumerable<AiContextItem> items)
    {
        var sanitizedItems = new List<AiContextItem>();
        var auditLog = new List<string>();

        foreach (var item in items)
        {
            string content = item.Content;
            bool wasRedacted = false;

            // 1. Redact Git credentials (never enter prompts)
            if (GitPersonalAccessTokenRegex.IsMatch(content))
            {
                content = GitPersonalAccessTokenRegex.Replace(content, match =>
                {
                    auditLog.Add($"Redacted Git personal access token in '{item.Label}'");
                    wasRedacted = true;
                    return "[REDACTED_GIT_CREDENTIAL]";
                });
            }

            if (GitRemoteCredentialRegex.IsMatch(content))
            {
                content = GitRemoteCredentialRegex.Replace(content, match =>
                {
                    auditLog.Add($"Redacted Git remote embedded credential in '{item.Label}'");
                    wasRedacted = true;
                    return $"https://[REDACTED_GIT_CREDENTIAL]@{match.Groups["host"].Value}";
                });
            }

            if (PrivateKeyRegex.IsMatch(content))
            {
                content = PrivateKeyRegex.Replace(content, match =>
                {
                    auditLog.Add($"Redacted private key in '{item.Label}'");
                    wasRedacted = true;
                    return "[REDACTED_PRIVATE_KEY]";
                });
            }

            // 2. Redact Authorization headers and generic tokens
            if (item.Kind == AiContextItemKind.RequestHeaders || item.Kind == AiContextItemKind.ResponseHeaders)
            {
                if (BearerOrBasicAuthRegex.IsMatch(content))
                {
                    content = BearerOrBasicAuthRegex.Replace(content, match =>
                    {
                        auditLog.Add($"Redacted Authorization header value in '{item.Label}'");
                        wasRedacted = true;
                        return $"{match.Groups[1].Value} [REDACTED_SECRET]";
                    });
                }
            }

            if (GenericApiKeyRegex.IsMatch(content))
            {
                content = GenericApiKeyRegex.Replace(content, match =>
                {
                    auditLog.Add($"Redacted secret key in '{item.Label}'");
                    wasRedacted = true;
                    return $"{match.Groups[1].Value}: \"[REDACTED_SECRET]\"";
                });
            }

            sanitizedItems.Add(new AiContextItem(item.Kind, item.Label, content, wasRedacted));
        }

        return new AiSanitizedContext(sanitizedItems, auditLog);
    }
}

public static class UnifiedDiffBuilder
{
    public static string CreateDiff(string original, string proposed, string filePath = "reviewable-change.txt")
    {
        var origLines = string.IsNullOrEmpty(original) ? Array.Empty<string>() : original.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var propLines = string.IsNullOrEmpty(proposed) ? Array.Empty<string>() : proposed.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

        var sb = new StringBuilder();
        sb.AppendLine($"--- a/{filePath}");
        sb.AppendLine($"+++ b/{filePath}");
        sb.AppendLine($"@@ -1,{Math.Max(1, origLines.Length)} +1,{Math.Max(1, propLines.Length)} @@");

        foreach (var l in origLines)
        {
            sb.AppendLine($"-{l}");
        }
        foreach (var l in propLines)
        {
            sb.AppendLine($"+{l}");
        }

        return sb.ToString();
    }
}

public sealed class AiAssistanceService : IDisposable
{
    private readonly AiConfiguration _config;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public AiAssistanceService(AiConfiguration config, HttpClient? httpClient = null)
    {
        _config = config;
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient();
            _ownsHttpClient = true;
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private void EnsureEnabled()
    {
        if (!_config.IsEnabled || _config.ProviderKind == AiProviderKind.Disabled)
        {
            throw new InvalidOperationException("AI assistance is optional and disabled until configured.");
        }

        if (string.IsNullOrWhiteSpace(_config.EndpointUrl))
        {
            throw new InvalidOperationException("AI provider endpoint URL is not configured.");
        }
    }

    public async Task<AiProposalDiff> DraftTestsAsync(
        IEnumerable<AiContextItem> contextItems,
        string userInstructions = "",
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        var sanitized = AiContextSanitizer.Sanitize(contextItems);
        var prompt = BuildPrompt("Draft test assertions and validation scripts for the provided API context.", sanitized, userInstructions);

        var proposedContent = await SendCompletionAsync(prompt, cancellationToken).ConfigureAwait(false);

        string original = string.Empty;
        var diff = UnifiedDiffBuilder.CreateDiff(original, proposedContent, "tests/drafted-assertions.js");

        return new AiProposalDiff
        {
            Kind = AiProposalKind.TestDraft,
            Title = "Drafted Test Assertions",
            Explanation = "Generated candidate assertions based on explicitly selected request/response context. User review is required.",
            OriginalText = original,
            ProposedText = proposedContent,
            UnifiedDiff = diff
        };
    }

    public async Task<AiProposalDiff> ExplainResponseAsync(
        IEnumerable<AiContextItem> contextItems,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        var sanitized = AiContextSanitizer.Sanitize(contextItems);
        var prompt = BuildPrompt("Explain the API response, breakdown status codes, diagnose possible errors, and summarize payload structure.", sanitized, "");

        var explanation = await SendCompletionAsync(prompt, cancellationToken).ConfigureAwait(false);

        return new AiProposalDiff
        {
            Kind = AiProposalKind.ResponseExplanation,
            Title = "API Response Explanation",
            Explanation = "Structural and behavioral explanation of the observed response.",
            OriginalText = string.Empty,
            ProposedText = explanation,
            UnifiedDiff = UnifiedDiffBuilder.CreateDiff("", explanation, "explanation.md")
        };
    }

    public async Task<AiProposalDiff> GenerateDatasetAsync(
        IReadOnlyList<string> columns,
        int rowCount,
        string scenarioDescription,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        var items = new List<AiContextItem>
        {
            new(AiContextItemKind.UserNotes, "Columns", string.Join(", ", columns)),
            new(AiContextItemKind.UserNotes, "RowCount", rowCount.ToString()),
            new(AiContextItemKind.UserNotes, "Scenario", scenarioDescription)
        };

        var sanitized = AiContextSanitizer.Sanitize(items);
        var prompt = BuildPrompt($"Generate a CSV dataset with columns: [{string.Join(", ", columns)}] for {rowCount} rows matching the scenario.", sanitized, "");

        var generatedCsv = await SendCompletionAsync(prompt, cancellationToken).ConfigureAwait(false);

        return new AiProposalDiff
        {
            Kind = AiProposalKind.DatasetGeneration,
            Title = "Generated Dataset",
            Explanation = $"Generated {rowCount} rows of test data for parameterized scenarios.",
            OriginalText = string.Empty,
            ProposedText = generatedCsv,
            UnifiedDiff = UnifiedDiffBuilder.CreateDiff("", generatedCsv, "dataset.csv")
        };
    }

    public async Task<AiProposalDiff> SuggestImportRepairAsync(
        string malformedContent,
        string errorDiagnostic,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        var items = new List<AiContextItem>
        {
            new(AiContextItemKind.RequestBody, "Malformed Content", malformedContent),
            new(AiContextItemKind.UserNotes, "Error Diagnostic", errorDiagnostic)
        };

        var sanitized = AiContextSanitizer.Sanitize(items);
        var prompt = BuildPrompt("Repair the malformed API definition or import payload so that it conforms to valid syntax.", sanitized, "");

        var repaired = await SendCompletionAsync(prompt, cancellationToken).ConfigureAwait(false);

        return new AiProposalDiff
        {
            Kind = AiProposalKind.ImportRepair,
            Title = "Import Repair Suggestion",
            Explanation = "Suggested fixes to resolve import syntax diagnostics. Requires user review before accepting.",
            OriginalText = malformedContent,
            ProposedText = repaired,
            UnifiedDiff = UnifiedDiffBuilder.CreateDiff(malformedContent, repaired, "imported-spec.json")
        };
    }

    public async Task<AiProposalDiff> SuggestScriptRefactoringAsync(
        string scriptContent,
        string goal,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();

        var items = new List<AiContextItem>
        {
            new(AiContextItemKind.ScriptCode, "Existing Script", scriptContent),
            new(AiContextItemKind.UserNotes, "Refactoring Goal", goal)
        };

        var sanitized = AiContextSanitizer.Sanitize(items);
        var prompt = BuildPrompt("Refactor the test or pre-request script according to modern standards and the specified goal.", sanitized, "");

        var refactored = await SendCompletionAsync(prompt, cancellationToken).ConfigureAwait(false);

        return new AiProposalDiff
        {
            Kind = AiProposalKind.ScriptRefactoring,
            Title = "Script Refactoring Suggestion",
            Explanation = "Refactored script. User review required prior to replacing script.",
            OriginalText = scriptContent,
            ProposedText = refactored,
            UnifiedDiff = UnifiedDiffBuilder.CreateDiff(scriptContent, refactored, "script.js")
        };
    }

    private static string BuildPrompt(string systemInstruction, AiSanitizedContext context, string userNotes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SYSTEM: {systemInstruction}");
        sb.AppendLine("NOTE: All sensitive credentials and tokens have been stripped. Output only the requested code or document.");
        sb.AppendLine();
        sb.AppendLine("SELECTED CONTEXT:");
        foreach (var item in context.Items)
        {
            sb.AppendLine($"--- [{item.Kind}] {item.Label} ---");
            sb.AppendLine(item.Content);
        }

        if (!string.IsNullOrWhiteSpace(userNotes))
        {
            sb.AppendLine("--- User Notes ---");
            sb.AppendLine(userNotes);
        }

        return sb.ToString();
    }

    private async Task<string> SendCompletionAsync(string prompt, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, _config.EndpointUrl);
        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        }

        var payload = new
        {
            model = _config.ModelName ?? "default-model",
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            temperature = _config.Temperature,
            max_tokens = _config.MaxTokens
        };

        string json = JsonSerializer.Serialize(payload);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await _httpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        string respJson = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(respJson);

        // OpenAI-compatible chat completion JSON format: choices[0].message.content
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var text))
            {
                return text.GetString() ?? string.Empty;
            }
        }

        return respJson;
    }
}
