var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5057");
var app = builder.Build();
app.MapMethods("/echo", ["GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"], async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    return Results.Json(new { request.Method, path = request.Path.Value, body = await reader.ReadToEndAsync(), status = "ready" });
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
