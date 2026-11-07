using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public interface IHttpTransport
{
    Task<HttpExecutionResult> SendAsync(HttpRequestDefinition request, ExecutionLimits limits, CancellationToken cancellationToken);
}

public sealed class HttpRequestService(IHttpTransport transport)
{
    public async Task<HttpExecutionResult> SendAsync(HttpRequestDefinition request, ExecutionLimits? limits = null,
        VariableContext? variableContext = null, CancellationToken cancellationToken = default)
    {
        var activeRequest = request;
        RedactionEngine? redaction = null;

        if (variableContext is not null)
        {
            var resolver = new VariableResolver();
            redaction = new RedactionEngine();
            redaction.RegisterSecrets(variableContext.DirectSecrets.Values);

            var resolvedUrl = await resolver.ResolveAsync(request.Url, variableContext, "URL", cancellationToken).ConfigureAwait(false);

            var resolvedHeaders = new List<HeaderEntry>();
            foreach (var h in request.Headers)
            {
                var name = await resolver.ResolveAsync(h.Name, variableContext, $"Header[{h.Name}].Name", cancellationToken).ConfigureAwait(false);
                var val = await resolver.ResolveAsync(h.Value, variableContext, $"Header[{h.Name}].Value", cancellationToken).ConfigureAwait(false);
                resolvedHeaders.Add(new HeaderEntry(name, val, h.Enabled));
            }

            var resolvedQuery = new List<QueryParamEntry>();
            foreach (var q in request.Query)
            {
                var name = await resolver.ResolveAsync(q.Name, variableContext, $"Query[{q.Name}].Name", cancellationToken).ConfigureAwait(false);
                var val = await resolver.ResolveAsync(q.Value, variableContext, $"Query[{q.Name}].Value", cancellationToken).ConfigureAwait(false);
                resolvedQuery.Add(new QueryParamEntry(name, val, q.Enabled));
            }

            RequestBodyDefinition? resolvedBody = null;
            if (request.BodyDefinition is not null)
            {
                var bodyText = request.BodyDefinition.Text is not null
                    ? await resolver.ResolveAsync(request.BodyDefinition.Text, variableContext, "Body", cancellationToken).ConfigureAwait(false)
                    : null;

                var resolvedFormData = new List<FormDataEntry>();
                foreach (var f in request.BodyDefinition.FormData)
                {
                    var fName = await resolver.ResolveAsync(f.Name, variableContext, $"FormData[{f.Name}].Name", cancellationToken).ConfigureAwait(false);
                    var fVal = await resolver.ResolveAsync(f.Value, variableContext, $"FormData[{f.Name}].Value", cancellationToken).ConfigureAwait(false);
                    resolvedFormData.Add(new FormDataEntry(fName, fVal, f.FilePath, f.ContentType, f.Enabled));
                }

                resolvedBody = new RequestBodyDefinition
                {
                    Mode = request.BodyDefinition.Mode,
                    Text = bodyText,
                    FilePath = request.BodyDefinition.FilePath,
                    FormData = resolvedFormData
                };
            }

            activeRequest = new HttpRequestDefinition
            {
                Id = request.Id,
                Name = request.Name,
                Description = request.Description,
                CollectionId = request.CollectionId,
                FolderId = request.FolderId,
                Protocol = request.Protocol,
                Method = request.Method,
                Url = resolvedUrl,
                Query = resolvedQuery,
                Headers = resolvedHeaders,
                BodyDefinition = resolvedBody,
                ContentType = request.ContentType,
                TimeoutMs = request.TimeoutMs,
                TransportOptions = request.TransportOptions,
                Auth = request.Auth
            };
        }
        else
        {
            redaction = new RedactionEngine();
        }

        var (authAppliedRequest, _) = await AuthApplier.ApplyAsync(activeRequest, variableContext, redaction, cancellationToken: cancellationToken).ConfigureAwait(false);
        activeRequest = authAppliedRequest;

        if (!Uri.TryCreate(activeRequest.Url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Use an absolute HTTP(S) URL without embedded credentials.", nameof(request));
        if (string.IsNullOrWhiteSpace(activeRequest.Method) || activeRequest.Method.Any(c => !IsToken(c)))
            throw new ArgumentException("The HTTP method must be a valid token.", nameof(request));
        if (activeRequest.TimeoutMs <= 0)
            throw new ArgumentException("Timeout must be greater than zero.", nameof(request));
        foreach (var header in activeRequest.Headers.Where(h => h.Enabled))
        {
            if (string.IsNullOrEmpty(header.Name) || header.Name.Any(c => !IsToken(c)) ||
                header.Value.Contains('\r') || header.Value.Contains('\n'))
                throw new ArgumentException("Header names must be tokens and values must not contain line breaks.", nameof(request));
        }
        limits ??= new ExecutionLimits();
        if (limits.PreviewBytes < 0 || limits.MaxResponseBytes <= 0 || limits.PreviewBytes > limits.MaxResponseBytes)
            throw new ArgumentException("Preview and response limits are inconsistent.", nameof(limits));

        var result = await transport.SendAsync(activeRequest, limits, cancellationToken).ConfigureAwait(false);
        return redaction is not null ? redaction.RedactResult(result) : result;
    }

    private static bool IsToken(char c) => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c);
}
