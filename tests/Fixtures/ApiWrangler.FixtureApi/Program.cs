var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5057");
var app = builder.Build();
app.MapMethods("/echo", ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"], async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();
    var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray());
    var query = request.Query.ToDictionary(q => q.Key, q => q.Value.ToArray());
    return Results.Json(new { request.Method, path = request.Path.Value, query, headers, body, status = "ready" });
});
app.MapGet("/delay", async (HttpContext context) =>
{
    await Task.Delay(10_000, context.RequestAborted);
    return Results.Ok(new { complete = true });
});
app.MapGet("/redirect", () => Results.Redirect("/echo"));
app.MapGet("/large", async (HttpContext context) =>
{
    context.Response.ContentType = "text/plain";
    var block = new byte[65536];
    Array.Fill(block, (byte)'x');
    for (var i = 0; i < 128; i++) await context.Response.Body.WriteAsync(block, context.RequestAborted);
});
app.Run();
