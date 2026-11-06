using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public interface IHttpTransport
{
    Task<HttpExecutionResult> SendAsync(HttpRequestDefinition request, ExecutionLimits limits, CancellationToken cancellationToken);
}

public sealed class HttpRequestService(IHttpTransport transport)
{
    public Task<HttpExecutionResult> SendAsync(HttpRequestDefinition request, ExecutionLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Use an absolute HTTP(S) URL without embedded credentials.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Method) || request.Method.Any(c => !IsToken(c)))
            throw new ArgumentException("The HTTP method must be a valid token.", nameof(request));
        if (request.TimeoutMs <= 0)
            throw new ArgumentException("Timeout must be greater than zero.", nameof(request));
        foreach (var header in request.Headers.Where(h => h.Enabled))
        {
            if (string.IsNullOrEmpty(header.Name) || header.Name.Any(c => !IsToken(c)) ||
                header.Value.Contains('\r') || header.Value.Contains('\n'))
                throw new ArgumentException("Header names must be tokens and values must not contain line breaks.", nameof(request));
        }
        limits ??= new ExecutionLimits();
        if (limits.PreviewBytes < 0 || limits.MaxResponseBytes <= 0 || limits.PreviewBytes > limits.MaxResponseBytes)
            throw new ArgumentException("Preview and response limits are inconsistent.", nameof(limits));
        return transport.SendAsync(request, limits, cancellationToken);
    }

    private static bool IsToken(char c) => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);
}
