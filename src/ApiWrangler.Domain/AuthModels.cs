namespace ApiWrangler.Domain;

public sealed record RequestAuthDefinition
{
    public string Type { get; init; } = "none";

    // Basic
    public string? Username { get; init; }
    public string? Password { get; init; }

    // Bearer
    public string? Token { get; init; }

    // ApiKey
    public string? ApiKeyName { get; init; }
    public string? ApiKeyValue { get; init; }
    public string ApiKeyLocation { get; init; } = "header"; // "header" or "query"

    // OAuth2
    public string? OAuthGrantType { get; init; } = "client_credentials"; // "client_credentials", "authorization_code", "refresh_token"
    public string? OAuthTokenUrl { get; init; }
    public string? OAuthClientId { get; init; }
    public string? OAuthClientSecret { get; init; }
    public string? OAuthScope { get; init; }
    public string? OAuthRefreshToken { get; init; }
    public string? OAuthAccessToken { get; init; }
    public DateTimeOffset? OAuthExpiresAt { get; init; }
    public bool OAuthUsePkce { get; init; }
    public string? OAuthCode { get; init; }
    public string? OAuthRedirectUri { get; init; }
    public string? OAuthCodeVerifier { get; init; }

    // Windows
    public bool UseDefaultWindowsCredentials { get; init; } = true;
    public string? WindowsDomain { get; init; }
    public string? WindowsUsername { get; init; }
    public string? WindowsPassword { get; init; }

    // Certificate
    public string? ClientCertificatePath { get; init; }
    public string? ClientCertificatePassword { get; init; }
}

public sealed class UnsupportedAuthException(string authType)
    : NotSupportedException($"Authentication type '{authType}' is unsupported. Supported types are: none, basic, bearer, apiKey, oauth2, windows, certificate.");
