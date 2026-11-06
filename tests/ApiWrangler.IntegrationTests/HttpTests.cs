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
    private HttpTransport transport = null!;
    private HttpRequestService service = null!;
    private string origin = "";
    private int redirectedCalls;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        app = builder.Build();
        app.MapPost("/echo", async (HttpContext context) =>
        {
            context.Response.Headers.Append("X-Fixture", "yes");
            await context.Request.Body.CopyToAsync(context.Response.Body);
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
        await app.StartAsync();
        origin = app.Urls.Single();
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
    public async Task Send_Redirect_DoesNotFollowOrForwardAuthorization()
    {
        var result = await service.SendAsync(new() { Url = origin + "/redirect", Headers = [new("Authorization", "Bearer private")] });
        Assert.Equal(302, result.StatusCode);
        Assert.Equal(0, redirectedCalls);
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
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
