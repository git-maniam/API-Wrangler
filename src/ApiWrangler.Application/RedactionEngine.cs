using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed class RedactionEngine
{
    private readonly HashSet<string> knownSecrets = new(StringComparer.Ordinal);

    public void RegisterSecret(string? secretValue)
    {
        if (!string.IsNullOrWhiteSpace(secretValue) && secretValue.Length >= 3)
        {
            knownSecrets.Add(secretValue);
        }
    }

    public void RegisterSecrets(IEnumerable<string> secretValues)
    {
        foreach (var val in secretValues) RegisterSecret(val);
    }

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text) || knownSecrets.Count == 0)
            return text;

        var result = text;
        foreach (var secret in knownSecrets)
        {
            if (result.Contains(secret, StringComparison.Ordinal))
            {
                result = result.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            }
        }
        return result;
    }

    public IReadOnlyList<HeaderEntry> RedactHeaders(IReadOnlyList<HeaderEntry> headers)
    {
        if (headers.Count == 0) return headers;

        var result = new List<HeaderEntry>(headers.Count);
        foreach (var header in headers)
        {
            var isSensitiveHeader = string.Equals(header.Name, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(header.Name, "Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(header.Name, "X-Api-Key", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(header.Name, "ApiKey", StringComparison.OrdinalIgnoreCase);

            if (isSensitiveHeader)
            {
                result.Add(new HeaderEntry(header.Name, "[REDACTED]", header.Enabled));
            }
            else
            {
                result.Add(new HeaderEntry(header.Name, Redact(header.Value), header.Enabled));
            }
        }
        return result;
    }

    public HttpExecutionResult RedactResult(HttpExecutionResult result)
    {
        var redactedHeaders = RedactHeaders(result.Headers);
        var redactedPreview = Redact(result.Preview);
        var redactedError = result.Error is not null ? Redact(result.Error) : null;

        return new HttpExecutionResult(
            result.Outcome,
            result.StatusCode,
            result.ReasonPhrase,
            redactedHeaders,
            redactedPreview,
            result.BytesReceived,
            result.PreviewTruncated,
            result.BodyPath,
            result.Duration,
            redactedError,
            result.Cookies,
            result.EffectiveUrl is not null ? Redact(result.EffectiveUrl) : null,
            result.RedirectCount);
    }
}

