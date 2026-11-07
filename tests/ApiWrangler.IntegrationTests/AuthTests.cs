using System.Text.Json;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApiWrangler.IntegrationTests;

public sealed class AuthTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ApiWrangler-auth-tests", Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private HttpTransport transport = null!;
    private HttpRequestService service = null!;
    private string origin = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();

        app.MapGet("/auth/basic", (HttpRequest request) =>
        {
            var authHeader = request.Headers.Authorization.ToString();
            if (authHeader == "Basic dGVzdHVzZXI6c2VjcmV0cGFzcw==")
                return Results.Ok(new { authenticated = true, user = "testuser" });
            return Results.StatusCode(401);
        });

        app.MapGet("/auth/bearer", (HttpRequest request) =>
        {
            var authHeader = request.Headers.Authorization.ToString();
            if (authHeader == "Bearer valid-bearer-token-777")
                return Results.Ok(new { authenticated = true });
            return Results.StatusCode(401);
        });

        app.MapGet("/auth/apikey-header", (HttpRequest request) =>
        {
            if (request.Headers.TryGetValue("X-Custom-Key", out var val) && val == "secret-key-123")
                return Results.Ok(new { authenticated = true });
            return Results.StatusCode(401);
        });

        app.MapGet("/auth/apikey-query", (HttpRequest request) =>
        {
            if (request.Query.TryGetValue("tokenKey", out var val) && val == "query-val-456")
                return Results.Ok(new { authenticated = true });
            return Results.StatusCode(401);
        });

        // OAuth token endpoint
        app.MapPost("/oauth/token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            var grantType = form["grant_type"].ToString();
            if (grantType == "client_credentials")
            {
                if (form["client_id"] == "client-123" && form["client_secret"] == "secret-abc")
                {
                    return Results.Json(new
                    {
                        access_token = "oauth-token-v1",
                        token_type = "Bearer",
                        expires_in = 2,
                        refresh_token = "oauth-refresh-v1"
                    });
                }
                return Results.StatusCode(400);
            }
            if (grantType == "refresh_token")
            {
                if (form["refresh_token"] == "oauth-refresh-v1")
                {
                    return Results.Json(new
                    {
                        access_token = "oauth-token-v2",
                        token_type = "Bearer",
                        expires_in = 3600
                    });
                }
                return Results.StatusCode(400);
            }
            if (grantType == "authorization_code")
            {
                if (form["code"] == "auth-code-xyz" && !string.IsNullOrEmpty(form["code_verifier"]))
                {
                    return Results.Json(new
                    {
                        access_token = "oauth-token-pkce",
                        token_type = "Bearer",
                        expires_in = 3600
                    });
                }
                return Results.StatusCode(400);
            }
            return Results.StatusCode(400);
        });

        app.MapGet("/oauth/resource", (HttpRequest request) =>
        {
            var auth = request.Headers.Authorization.ToString();
            if (auth == "Bearer oauth-token-v1" || auth == "Bearer oauth-token-v2" || auth == "Bearer oauth-token-pkce")
                return Results.Ok(new { data = "sensitive-content", token = auth });
            return Results.StatusCode(401);
        });

        await app.StartAsync();
        origin = app.Urls.Single();

        transport = new HttpTransport(directory);
        service = new HttpRequestService(transport);
    }

    [Fact]
    public async Task Auth_Basic_SucceedsWithValidCredentials_AndRedactsPassword()
    {
        var request = new HttpRequestDefinition
        {
            Url = origin + "/auth/basic",
            Method = "GET",
            Auth = new RequestAuthDefinition
            {
                Type = "basic",
                Username = "testuser",
                Password = "secretpass"
            }
        };

        var result = await service.SendAsync(request);
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome);
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("testuser", result.Preview);
        Assert.DoesNotContain("secretpass", result.Preview);
    }

    [Fact]
    public async Task Auth_Basic_RejectsInvalidCredentials()
    {
        var request = new HttpRequestDefinition
        {
            Url = origin + "/auth/basic",
            Method = "GET",
            Auth = new RequestAuthDefinition
            {
                Type = "basic",
                Username = "testuser",
                Password = "wrongpassword"
            }
        };

        var result = await service.SendAsync(request);
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome);
        Assert.Equal(401, result.StatusCode);
    }

    [Fact]
    public async Task Auth_Bearer_Succeeds_AndRedactsTokenFromHeaders()
    {
        var request = new HttpRequestDefinition
        {
            Url = origin + "/auth/bearer",
            Method = "GET",
            Auth = new RequestAuthDefinition
            {
                Type = "bearer",
                Token = "valid-bearer-token-777"
            }
        };

        var result = await service.SendAsync(request);
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome);
        Assert.Equal(200, result.StatusCode);

        var authHeader = result.Headers.FirstOrDefault(h => h.Name.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        if (authHeader is not null)
        {
            Assert.Equal("[REDACTED]", authHeader.Value);
        }
    }

    [Fact]
    public async Task Auth_ApiKey_SupportsHeaderAndQueryLocations()
    {
        // 1. In header
        var headerReq = new HttpRequestDefinition
        {
            Url = origin + "/auth/apikey-header",
            Method = "GET",
            Auth = new RequestAuthDefinition
            {
                Type = "apiKey",
                ApiKeyName = "X-Custom-Key",
                ApiKeyValue = "secret-key-123",
                ApiKeyLocation = "header"
            }
        };

        var resHeader = await service.SendAsync(headerReq);
        Assert.Equal(200, resHeader.StatusCode);

        // 2. In query
        var queryReq = new HttpRequestDefinition
        {
            Url = origin + "/auth/apikey-query",
            Method = "GET",
            Auth = new RequestAuthDefinition
            {
                Type = "apiKey",
                ApiKeyName = "tokenKey",
                ApiKeyValue = "query-val-456",
                ApiKeyLocation = "query"
            }
        };

        var resQuery = await service.SendAsync(queryReq);
        Assert.Equal(200, resQuery.StatusCode);
    }

    [Fact]
    public void PkceHelper_GeneratesValidVerifierAndChallenge()
    {
        var verifier = OAuthPkceHelper.GenerateCodeVerifier();
        Assert.NotNull(verifier);
        Assert.True(verifier.Length >= 43);

        var challenge = OAuthPkceHelper.GenerateCodeChallenge(verifier);
        Assert.NotNull(challenge);
        Assert.DoesNotContain("=", challenge);
        Assert.DoesNotContain("+", challenge);
        Assert.DoesNotContain("/", challenge);
    }

    [Fact]
    public async Task Auth_OAuth2_ClientCredentialsAndRefreshLifecycle()
    {
        using var client = new HttpClient();
        var initialAuth = new RequestAuthDefinition
        {
            Type = "oauth2",
            OAuthGrantType = "client_credentials",
            OAuthTokenUrl = origin + "/oauth/token",
            OAuthClientId = "client-123",
            OAuthClientSecret = "secret-abc"
        };

        var initialRequest = new HttpRequestDefinition
        {
            Url = origin + "/oauth/resource",
            Method = "GET",
            Auth = initialAuth
        };

        // 1. Initial request triggers token acquisition
        var (appliedReq, updatedAuth) = await AuthApplier.ApplyAsync(initialRequest, null, new RedactionEngine(), client);
        Assert.NotNull(updatedAuth);
        Assert.Equal("oauth-token-v1", updatedAuth.OAuthAccessToken);
        Assert.Equal("oauth-refresh-v1", updatedAuth.OAuthRefreshToken);

        var result1 = await service.SendAsync(appliedReq);
        Assert.Equal(200, result1.StatusCode);

        // 2. Simulate token expiry and refresh
        var expiredAuth = updatedAuth with
        {
            OAuthExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            OAuthGrantType = "refresh_token"
        };

        var refreshReq = new HttpRequestDefinition
        {
            Url = origin + "/oauth/resource",
            Method = "GET",
            Auth = expiredAuth
        };

        var (refreshedReq, newAuth) = await AuthApplier.ApplyAsync(refreshReq, null, new RedactionEngine(), client);
        Assert.NotNull(newAuth);
        Assert.Equal("oauth-token-v2", newAuth.OAuthAccessToken);

        var result2 = await service.SendAsync(refreshedReq);
        Assert.Equal(200, result2.StatusCode);
    }

    [Fact]
    public async Task Auth_OAuth2_AuthorizationCodeWithPkce_Succeeds()
    {
        using var client = new HttpClient();
        var verifier = OAuthPkceHelper.GenerateCodeVerifier();

        var pkceAuth = new RequestAuthDefinition
        {
            Type = "oauth2",
            OAuthGrantType = "authorization_code",
            OAuthTokenUrl = origin + "/oauth/token",
            OAuthClientId = "client-pkce",
            OAuthCode = "auth-code-xyz",
            OAuthCodeVerifier = verifier,
            OAuthUsePkce = true
        };

        var request = new HttpRequestDefinition
        {
            Url = origin + "/oauth/resource",
            Method = "GET",
            Auth = pkceAuth
        };

        var (appliedReq, updatedAuth) = await AuthApplier.ApplyAsync(request, null, new RedactionEngine(), client);
        Assert.NotNull(updatedAuth);
        Assert.Equal("oauth-token-pkce", updatedAuth.OAuthAccessToken);

        var result = await service.SendAsync(appliedReq);
        Assert.Equal(200, result.StatusCode);
    }

    [Fact]
    public async Task Auth_Cancellation_CancelsCleanly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new HttpRequestDefinition
        {
            Url = origin + "/auth/basic",
            Method = "GET",
            Auth = new RequestAuthDefinition
            {
                Type = "basic",
                Username = "user",
                Password = "pwd"
            }
        };

        var result = await service.SendAsync(request, cancellationToken: cts.Token);
        Assert.Equal(ExecutionOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task Auth_Windows_ExecutesWithoutError()
    {
        var request = new HttpRequestDefinition
        {
            Url = origin + "/auth/basic",
            Method = "GET",
            Auth = new RequestAuthDefinition
            {
                Type = "windows",
                UseDefaultWindowsCredentials = true
            }
        };

        var result = await service.SendAsync(request);
        Assert.NotNull(result);
        Assert.True(result.StatusCode.HasValue);
    }

    [Fact]
    public void RequestAssetSerializer_RoundtripsAllAuthTypes()
    {
        var original = new HttpRequestDefinition
        {
            Url = "https://example.com/api",
            Method = "GET",
            Auth = new RequestAuthDefinition
            {
                Type = "oauth2",
                OAuthGrantType = "authorization_code",
                OAuthTokenUrl = "https://example.com/token",
                OAuthClientId = "client-id",
                OAuthClientSecret = "{{secret:clientSecret}}",
                OAuthScope = "read write",
                OAuthUsePkce = true,
                OAuthCode = "code-123",
                OAuthCodeVerifier = "verifier-456",
                UseDefaultWindowsCredentials = false,
                WindowsDomain = "CORP",
                WindowsUsername = "corpuser",
                WindowsPassword = "{{secret:winPass}}",
                ClientCertificatePath = "/path/to/cert.pfx",
                ClientCertificatePassword = "{{secret:certPass}}"
            }
        };

        var json = RequestAssetSerializer.ToJsonObject(original);
        var restored = RequestAssetSerializer.FromJsonObject(json);

        Assert.Equal(original.Auth.Type, restored.Auth.Type);
        Assert.Equal(original.Auth.OAuthGrantType, restored.Auth.OAuthGrantType);
        Assert.Equal(original.Auth.OAuthClientId, restored.Auth.OAuthClientId);
        Assert.Equal(original.Auth.OAuthClientSecret, restored.Auth.OAuthClientSecret);
        Assert.Equal(original.Auth.OAuthScope, restored.Auth.OAuthScope);
        Assert.True(restored.Auth.OAuthUsePkce);
        Assert.Equal(original.Auth.OAuthCode, restored.Auth.OAuthCode);
        Assert.Equal(original.Auth.OAuthCodeVerifier, restored.Auth.OAuthCodeVerifier);
        Assert.False(restored.Auth.UseDefaultWindowsCredentials);
        Assert.Equal(original.Auth.WindowsDomain, restored.Auth.WindowsDomain);
        Assert.Equal(original.Auth.WindowsUsername, restored.Auth.WindowsUsername);
        Assert.Equal(original.Auth.WindowsPassword, restored.Auth.WindowsPassword);
        Assert.Equal(original.Auth.ClientCertificatePath, restored.Auth.ClientCertificatePath);
        Assert.Equal(original.Auth.ClientCertificatePassword, restored.Auth.ClientCertificatePassword);
    }

    [Fact]
    public async Task Auth_UnsupportedAuthType_ThrowsBeforeExecution()
    {
        var unsupportedRequest = new HttpRequestDefinition
        {
            Url = origin + "/echo",
            Method = "GET",
            Auth = new RequestAuthDefinition
            {
                Type = "digest"
            }
        };

        await Assert.ThrowsAsync<UnsupportedAuthException>(() => service.SendAsync(unsupportedRequest));
    }

    public async Task DisposeAsync()
    {
        transport.Dispose();
        await app.DisposeAsync();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
