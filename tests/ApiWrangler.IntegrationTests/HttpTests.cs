using System.IO.Compression;
using System.Text.Json;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApiWrangler.IntegrationTests;

public sealed class HttpTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ApiWrangler-tests", Guid.NewGuid().ToString("N"));
    private WebApplication app = null!;
    private WebApplication app2 = null!;
    private HttpTransport transport = null!;
    private HttpRequestService service = null!;
    private string origin = "";
    private string origin2 = "";
    private int redirectedCalls;
    private bool crossOriginAuthReceived;
    private int nonIdempotentPostAttempts;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();

        app.MapPost("/echo", async (HttpContext context) =>
        {
            context.Response.Headers.Append("X-Fixture", "yes");
            await context.Request.Body.CopyToAsync(context.Response.Body);
        });

        app.MapMethods("/echo-inspect", ["GET", "POST", "PUT"], async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray());
            var query = request.Query.ToDictionary(q => q.Key, q => q.Value.ToArray());
            return Results.Json(new { request.Method, query, headers, body, contentType = request.ContentType });
        });

        app.MapGet("/redirect", () => Results.Redirect("/target"));
        app.MapGet("/target", () => { Interlocked.Increment(ref redirectedCalls); return Results.Ok(); });

        app.MapGet("/delay", async (HttpContext context) =>
        {
            await Task.Delay(10_000, context.RequestAborted);
            await context.Response.WriteAsync("done", context.RequestAborted);
        });

        app.MapGet("/bytes", () => Results.Text(new string('x', 100_000)));
        app.MapGet("/failure", () => Results.StatusCode(500));

        app.MapGet("/cookies", (HttpResponse response) =>
        {
            response.Headers.Append("Set-Cookie", "session_token=secret987; Path=/; HttpOnly");
            response.Headers.Append("Set-Cookie", "display_theme=dark; Path=/app");
            return Results.Ok();
        });

        app.MapGet("/gzip", async (HttpContext context) =>
        {
            context.Response.Headers.Append("Content-Encoding", "gzip");
            context.Response.ContentType = "text/plain";
            await using var gzip = new GZipStream(context.Response.Body, CompressionLevel.Fastest);
            await gzip.WriteAsync(System.Text.Encoding.UTF8.GetBytes("decompressed-successfully"));
        });

        app.MapPost("/non-idempotent-fail", () =>
        {
            Interlocked.Increment(ref nonIdempotentPostAttempts);
            return Results.StatusCode(500);
        });

        await app.StartAsync();
        origin = app.Urls.Single();

        // Second server to test cross-origin redirect
        var builder2 = WebApplication.CreateBuilder();
        builder2.Logging.ClearProviders();
        builder2.WebHost.UseUrls("http://127.0.0.1:0");
        app2 = builder2.Build();
        app2.MapGet("/target-cross", (HttpRequest request) =>
        {
            crossOriginAuthReceived = request.Headers.ContainsKey("Authorization");
            return Results.Ok(new { received = true });
        });
        await app2.StartAsync();
        origin2 = app2.Urls.Single();

        app.MapGet("/redirect-cross", () => Results.Redirect(origin2 + "/target-cross"));

        transport = new HttpTransport(directory);
        service = new HttpRequestService(transport);
    }

    [Fact]
    public async Task Send_PostBody_PreservesBytesAndHeaders()
    {
        var result = await service.SendAsync(new() { Url = origin + "/echo", Method = "POST", Body = "{\"name\":\"café\"}" });
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome);
        Assert.Equal(200, result.StatusCode);
        Assert.Equal("{\"name\":\"café\"}", await File.ReadAllTextAsync(result.BodyPath!));
        Assert.Contains(result.Headers, h => h.Name == "X-Fixture" && h.Value == "yes");
    }

    [Fact]
    public async Task Send_RepeatedHeadersAndQueryParams_ArePreservedAndTransmitted()
    {
        var request = new HttpRequestDefinition
        {
            Url = origin + "/echo-inspect",
            Method = "GET",
            Headers =
            [
                new("X-Custom-Tag", "alpha"),
                new("X-Custom-Tag", "beta")
            ],
            Query =
            [
                new("tag", "first"),
                new("tag", "second")
            ]
        };

        var result = await service.SendAsync(request);
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome);
        using var doc = JsonDocument.Parse(result.Preview);
        var root = doc.RootElement;

        var tags = root.GetProperty("query").GetProperty("tag").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["first", "second"], tags);

        var customHeaders = root.GetProperty("headers").GetProperty("X-Custom-Tag").EnumerateArray()
            .SelectMany(e => e.GetString()!.Split(',')).Select(s => s.Trim()).ToArray();
        Assert.Equal(2, customHeaders.Length);
        Assert.Contains("alpha", customHeaders);
        Assert.Contains("beta", customHeaders);
    }

    [Fact]
    public async Task Send_MultipartFormData_TransmitsFormAndFileFields()
    {
        var tempFile = Path.Combine(directory, "upload.txt");
        await File.WriteAllTextAsync(tempFile, "sample-file-content");

        var request = new HttpRequestDefinition
        {
            Url = origin + "/echo-inspect",
            Method = "POST",
            BodyDefinition = new RequestBodyDefinition
            {
                Mode = "form-data",
                FormData =
                [
                    new("username", "testuser"),
                    new("file", "", tempFile, "text/plain")
                ]
            }
        };

        var result = await service.SendAsync(request);
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome);
        using var doc = JsonDocument.Parse(result.Preview);
        var contentType = doc.RootElement.GetProperty("contentType").GetString();
        Assert.NotNull(contentType);
        Assert.StartsWith("multipart/form-data", contentType);
        var body = doc.RootElement.GetProperty("body").GetString();
        Assert.Contains("testuser", body);
        Assert.Contains("sample-file-content", body);
    }

    [Fact]
    public async Task Send_UrlEncodedForm_TransmitsCorrectContentTypeAndBody()
    {
        var request = new HttpRequestDefinition
        {
            Url = origin + "/echo-inspect",
            Method = "POST",
            BodyDefinition = new RequestBodyDefinition
            {
                Mode = "urlencoded",
                FormData =
                [
                    new("query", "hello world"),
                    new("category", "books & media")
                ]
            }
        };

        var result = await service.SendAsync(request);
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome);
        using var doc = JsonDocument.Parse(result.Preview);
        var contentType = doc.RootElement.GetProperty("contentType").GetString();
        Assert.Equal("application/x-www-form-urlencoded", contentType);
        var body = doc.RootElement.GetProperty("body").GetString();
        Assert.Equal("query=hello+world&category=books+%26+media", body);
    }

    [Fact]
    public async Task Send_FileBackedBody_StreamsDirectlyFromFile()
    {
        var tempFile = Path.Combine(directory, "raw-binary.dat");
        var bytes = System.Text.Encoding.UTF8.GetBytes("streamed-file-payload");
        await File.WriteAllBytesAsync(tempFile, bytes);

        var request = new HttpRequestDefinition
        {
            Url = origin + "/echo-inspect",
            Method = "POST",
            ContentType = "application/octet-stream",
            BodyDefinition = new RequestBodyDefinition
            {
                Mode = "file",
                FilePath = tempFile
            }
        };

        var result = await service.SendAsync(request);
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome);
        using var doc = JsonDocument.Parse(result.Preview);
        var body = doc.RootElement.GetProperty("body").GetString();
        Assert.Equal("streamed-file-payload", body);
    }

    [Fact]
    public async Task Send_Compression_DecompressesGzipResponseAutomatically()
    {
        var request = new HttpRequestDefinition
        {
            Url = origin + "/gzip",
            Method = "GET"
        };

        var result = await service.SendAsync(request);
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome);
        Assert.Equal("decompressed-successfully", result.Preview);
    }

    [Fact]
    public async Task Send_Cookies_ParsesSetCookieHeaders()
    {
        var request = new HttpRequestDefinition
        {
            Url = origin + "/cookies",
            Method = "GET"
        };

        var result = await service.SendAsync(request);
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome);
        Assert.NotNull(result.Cookies);
        Assert.Equal(2, result.Cookies.Count);
        var session = result.Cookies.Single(c => c.Name == "session_token");
        Assert.Equal("secret987", session.Value);
        Assert.True(session.HttpOnly);
        var theme = result.Cookies.Single(c => c.Name == "display_theme");
        Assert.Equal("dark", theme.Value);
        Assert.Equal("/app", theme.Path);
    }

    [Fact]
    public async Task Send_Redirect_DoesNotFollowOrForwardAuthorization_ByDefault()
    {
        var result = await service.SendAsync(new() { Url = origin + "/redirect", Headers = [new("Authorization", "Bearer private")] });
        Assert.Equal(302, result.StatusCode);
        Assert.Equal(0, redirectedCalls);
    }

    [Fact]
    public async Task Send_FollowRedirect_CrossOrigin_StripsAuthorizationHeader()
    {
        crossOriginAuthReceived = false;
        var request = new HttpRequestDefinition
        {
            Url = origin + "/redirect-cross",
            Method = "GET",
            Headers = [new("Authorization", "Bearer sensitive-auth-token")],
            TransportOptions = new HttpTransportOptions
            {
                FollowRedirects = true,
                StripAuthOnCrossOriginRedirect = true
            }
        };

        var result = await service.SendAsync(request);
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome);
        Assert.Equal(200, result.StatusCode);
        Assert.False(crossOriginAuthReceived, "Authorization header must be stripped when crossing origins.");
    }

    [Fact]
    public async Task Send_NonIdempotentFailure_DoesNotAutomaticallyRetry()
    {
        nonIdempotentPostAttempts = 0;
        var request = new HttpRequestDefinition
        {
            Url = origin + "/non-idempotent-fail",
            Method = "POST",
            Body = "{\"action\":\"charge\"}"
        };

        var result = await service.SendAsync(request);
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome); // HTTP 500 is a transport pass with 500 status
        Assert.Equal(500, result.StatusCode);
        Assert.Equal(1, nonIdempotentPostAttempts);
    }

    [Fact]
    public async Task Send_ServerError_PreservesHttpStatusWithoutInventingAssertions()
    {
        var result = await service.SendAsync(new() { Url = origin + "/failure" });
        Assert.Equal(500, result.StatusCode);
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome);
    }

    [Fact]
    public async Task Send_LargeResponse_BoundsPreviewAndStreamsCompleteBody()
    {
        var result = await service.SendAsync(new() { Url = origin + "/bytes" }, new() { PreviewBytes = 512 });
        Assert.Equal(ExecutionOutcome.Passed, result.Outcome);
        Assert.Equal(512, result.Preview.Length);
        Assert.True(result.PreviewTruncated);
        Assert.Equal(100_000, new FileInfo(result.BodyPath!).Length);
    }

    [Fact]
    public async Task Send_ResponseLimit_RemovesPartialArtifact()
    {
        var result = await service.SendAsync(new() { Url = origin + "/bytes" }, new() { PreviewBytes = 512, MaxResponseBytes = 1024 });
        Assert.Equal(ExecutionOutcome.Error, result.Outcome);
        Assert.Null(result.BodyPath);
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public async Task Send_Deadline_IsNotUserCancellation()
    {
        var result = await service.SendAsync(new() { Url = origin + "/delay", TimeoutMs = 100 });
        Assert.Equal(ExecutionOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public async Task Send_UserCancellation_IsNotTimeoutOrSuccess()
    {
        using var cancellation = new CancellationTokenSource(100);
        var result = await service.SendAsync(new() { Url = origin + "/delay" }, cancellationToken: cancellation.Token);
        Assert.Equal(ExecutionOutcome.Cancelled, result.Outcome);
    }

    [Theory]
    [InlineData("file:///C:/private.txt")]
    [InlineData("https://name:secret@example.test/")]
    public async Task Send_UnsafeUrl_FailsBeforeTransport(string url) =>
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendAsync(new() { Url = url }));

    [Fact]
    public async Task Send_HeaderInjection_FailsBeforeTransport() =>
        await Assert.ThrowsAsync<ArgumentException>(() => service.SendAsync(new()
        { Url = origin, Headers = [new("X-Example", "value\r\nInjected: yes")] }));

    public async Task DisposeAsync()
    {
        transport.Dispose();
        await app.DisposeAsync();
        await app2.DisposeAsync();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
