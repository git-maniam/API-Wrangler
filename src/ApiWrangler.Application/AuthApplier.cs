using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public static class OAuthPkceHelper
{
    public static string GenerateCodeVerifier(int byteLength = 32)
    {
        byte[] bytes = new byte[byteLength];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string GenerateCodeChallenge(string codeVerifier)
    {
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(hash)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}

public static class AuthApplier
{
    private static readonly HttpClient DefaultTokenClient = new();

    private static readonly HashSet<string> SupportedAuthTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "basic", "bearer", "apikey", "oauth2", "windows", "certificate"
    };

    public static async Task<(HttpRequestDefinition Request, RequestAuthDefinition? UpdatedAuth)> ApplyAsync(
        HttpRequestDefinition request,
        VariableContext? variableContext,
        RedactionEngine? redaction,
        HttpClient? tokenClient = null,
        CancellationToken cancellationToken = default)
    {
        var auth = request.Auth;
        if (auth is null || string.Equals(auth.Type, "none", StringComparison.OrdinalIgnoreCase))
            return (request, null);

        if (!SupportedAuthTypes.Contains(auth.Type))
            throw new UnsupportedAuthException(auth.Type);

        var resolver = new VariableResolver();
        var ctx = variableContext ?? new VariableContext();

        var headers = new List<HeaderEntry>(request.Headers);
        var query = new List<QueryParamEntry>(request.Query);
        RequestAuthDefinition? updatedAuth = null;

        switch (auth.Type.ToLowerInvariant())
        {
            case "basic":
                var user = string.IsNullOrEmpty(auth.Username)
                    ? ""
                    : await resolver.ResolveAsync(auth.Username, ctx, "Auth:Basic.Username", cancellationToken).ConfigureAwait(false);
                var pass = string.IsNullOrEmpty(auth.Password)
                    ? ""
                    : await resolver.ResolveAsync(auth.Password, ctx, "Auth:Basic.Password", cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(pass)) redaction?.RegisterSecret(pass);

                var basicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}"));
                headers.RemoveAll(h => string.Equals(h.Name, "Authorization", StringComparison.OrdinalIgnoreCase));
                headers.Add(new HeaderEntry("Authorization", $"Basic {basicToken}"));
                break;

            case "bearer":
                var token = string.IsNullOrEmpty(auth.Token)
                    ? ""
                    : await resolver.ResolveAsync(auth.Token, ctx, "Auth:Bearer.Token", cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(token)) redaction?.RegisterSecret(token);

                headers.RemoveAll(h => string.Equals(h.Name, "Authorization", StringComparison.OrdinalIgnoreCase));
                headers.Add(new HeaderEntry("Authorization", $"Bearer {token}"));
                break;

            case "apikey":
                var keyName = string.IsNullOrEmpty(auth.ApiKeyName)
                    ? "X-Api-Key"
                    : await resolver.ResolveAsync(auth.ApiKeyName, ctx, "Auth:ApiKey.Name", cancellationToken).ConfigureAwait(false);
                var keyVal = string.IsNullOrEmpty(auth.ApiKeyValue)
                    ? ""
                    : await resolver.ResolveAsync(auth.ApiKeyValue, ctx, "Auth:ApiKey.Value", cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(keyVal)) redaction?.RegisterSecret(keyVal);

                if (string.Equals(auth.ApiKeyLocation, "query", StringComparison.OrdinalIgnoreCase))
                {
                    query.RemoveAll(q => string.Equals(q.Name, keyName, StringComparison.OrdinalIgnoreCase));
                    query.Add(new QueryParamEntry(keyName, keyVal));
                }
                else
                {
                    headers.RemoveAll(h => string.Equals(h.Name, keyName, StringComparison.OrdinalIgnoreCase));
                    headers.Add(new HeaderEntry(keyName, keyVal));
                }
                break;

            case "oauth2":
                var tokenUrl = string.IsNullOrEmpty(auth.OAuthTokenUrl)
                    ? null
                    : await resolver.ResolveAsync(auth.OAuthTokenUrl, ctx, "Auth:OAuth2.TokenUrl", cancellationToken).ConfigureAwait(false);
                var clientId = string.IsNullOrEmpty(auth.OAuthClientId)
                    ? null
                    : await resolver.ResolveAsync(auth.OAuthClientId, ctx, "Auth:OAuth2.ClientId", cancellationToken).ConfigureAwait(false);
                var clientSecret = string.IsNullOrEmpty(auth.OAuthClientSecret)
                    ? null
                    : await resolver.ResolveAsync(auth.OAuthClientSecret, ctx, "Auth:OAuth2.ClientSecret", cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(clientSecret)) redaction?.RegisterSecret(clientSecret);

                string? activeAccessToken = auth.OAuthAccessToken;
                bool isExpired = auth.OAuthExpiresAt.HasValue && auth.OAuthExpiresAt.Value <= DateTimeOffset.UtcNow;

                var effectiveTokenClient = tokenClient ?? DefaultTokenClient;

                if ((string.IsNullOrEmpty(activeAccessToken) || isExpired) && !string.IsNullOrEmpty(tokenUrl))
                {
                    var grantType = auth.OAuthGrantType?.ToLowerInvariant() ?? "client_credentials";
                    var tokenPairs = new Dictionary<string, string>();

                    if (grantType == "refresh_token" && !string.IsNullOrEmpty(auth.OAuthRefreshToken))
                    {
                        tokenPairs["grant_type"] = "refresh_token";
                        tokenPairs["refresh_token"] = auth.OAuthRefreshToken;
                    }
                    else if (grantType == "authorization_code")
                    {
                        tokenPairs["grant_type"] = "authorization_code";
                        if (!string.IsNullOrEmpty(auth.OAuthCode)) tokenPairs["code"] = auth.OAuthCode;
                        if (!string.IsNullOrEmpty(auth.OAuthRedirectUri)) tokenPairs["redirect_uri"] = auth.OAuthRedirectUri;
                        if (!string.IsNullOrEmpty(auth.OAuthCodeVerifier)) tokenPairs["code_verifier"] = auth.OAuthCodeVerifier;
                    }
                    else
                    {
                        tokenPairs["grant_type"] = "client_credentials";
                    }

                    if (!string.IsNullOrEmpty(clientId)) tokenPairs["client_id"] = clientId;
                    if (!string.IsNullOrEmpty(clientSecret)) tokenPairs["client_secret"] = clientSecret;
                    if (!string.IsNullOrEmpty(auth.OAuthScope)) tokenPairs["scope"] = auth.OAuthScope;

                    using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
                    {
                        Content = new FormUrlEncodedContent(tokenPairs)
                    };

                    using var tokenResponse = await effectiveTokenClient.SendAsync(tokenRequest, cancellationToken).ConfigureAwait(false);
                    if (tokenResponse.IsSuccessStatusCode)
                    {
                        var json = await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("access_token", out var atElem))
                            activeAccessToken = atElem.GetString();

                        string? newRefreshToken = auth.OAuthRefreshToken;
                        if (root.TryGetProperty("refresh_token", out var rtElem))
                            newRefreshToken = rtElem.GetString();

                        int expiresIn = 3600;
                        if (root.TryGetProperty("expires_in", out var expElem) && expElem.TryGetInt32(out var sec))
                            expiresIn = sec;

                        updatedAuth = auth with
                        {
                            OAuthAccessToken = activeAccessToken,
                            OAuthRefreshToken = newRefreshToken,
                            OAuthExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 10)
                        };
                    }
                    else
                    {
                        throw new InvalidOperationException($"OAuth 2.0 token endpoint returned HTTP {(int)tokenResponse.StatusCode}.");
                    }
                }

                if (!string.IsNullOrEmpty(activeAccessToken))
                {
                    redaction?.RegisterSecret(activeAccessToken);
                    headers.RemoveAll(h => string.Equals(h.Name, "Authorization", StringComparison.OrdinalIgnoreCase));
                    headers.Add(new HeaderEntry("Authorization", $"Bearer {activeAccessToken}"));
                }
                break;

            case "windows":
                if (!string.IsNullOrEmpty(auth.WindowsPassword))
                {
                    redaction?.RegisterSecret(auth.WindowsPassword);
                }
                break;

            case "certificate":
                if (!string.IsNullOrEmpty(auth.ClientCertificatePassword))
                {
                    redaction?.RegisterSecret(auth.ClientCertificatePassword);
                }
                break;
        }

        var modifiedRequest = new HttpRequestDefinition
        {
            Id = request.Id,
            Name = request.Name,
            Description = request.Description,
            CollectionId = request.CollectionId,
            FolderId = request.FolderId,
            Protocol = request.Protocol,
            Method = request.Method,
            Url = request.Url,
            Query = query,
            Headers = headers,
            BodyDefinition = request.BodyDefinition,
            ContentType = request.ContentType,
            TimeoutMs = request.TimeoutMs,
            TransportOptions = request.TransportOptions,
            Auth = updatedAuth ?? auth
        };

        return (modifiedRequest, updatedAuth);
    }
}
