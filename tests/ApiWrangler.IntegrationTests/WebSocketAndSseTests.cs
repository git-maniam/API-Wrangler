using System.Net.WebSockets;
using System.Text;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class WebSocketAndSseTests
{
    [Fact]
    public async Task WebSocket_Connect_Send_Receive_And_Disconnect_Works()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                var buffer = new byte[1024 * 4];
                while (ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        break;
                    }

                    string msg = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    string reply = $"Echo: {msg}";
                    byte[] replyBytes = Encoding.UTF8.GetBytes(reply);
                    await ws.SendAsync(new ArraySegment<byte>(replyBytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            else
            {
                context.Response.StatusCode = 400;
            }
        });

        await app.StartAsync();

        try
        {
            string httpUrl = app.Urls.First();
            var wsUri = new Uri(httpUrl.Replace("http://", "ws://") + "/ws");

            await using var session = new WebSocketSession();
            await session.ConnectAsync(wsUri);

            Assert.Equal(WebSocketState.Open, session.State);

            await session.SendMessageAsync("Hello Server!");

            string received = await session.AwaitMessageAsync(m => m.Contains("Echo: Hello Server!"), TimeSpan.FromSeconds(5));
            Assert.Equal("Echo: Hello Server!", received);

            await session.DisconnectAsync();
            Assert.Contains(session.Transcript, t => t.EventType == StreamEventType.Disconnected);
            Assert.Contains(session.Transcript, t => t.EventType == StreamEventType.MessageSent);
            Assert.Contains(session.Transcript, t => t.EventType == StreamEventType.MessageReceived);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task WebSocket_BoundedRetention_DropsOldestMessages()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.UseWebSockets();
        app.Map("/ws", async context =>
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                using var ws = await context.WebSockets.AcceptWebSocketAsync();
                for (int i = 1; i <= 10; i++)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes($"msg-{i}");
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
                }
                var buf = new byte[100];
                await ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
            }
        });

        await app.StartAsync();

        try
        {
            string httpUrl = app.Urls.First();
            var wsUri = new Uri(httpUrl.Replace("http://", "ws://") + "/ws");

            // Limit to 3 messages
            var limits = new StreamingLimits(MaxMessages: 3);
            await using var session = new WebSocketSession(limits);
            await session.ConnectAsync(wsUri);

            // Wait for msg-10
            string lastMsg = await session.AwaitMessageAsync(m => m == "msg-10", TimeSpan.FromSeconds(5));
            Assert.Equal("msg-10", lastMsg);

            Assert.Equal(3, session.ReceivedMessages.Count);
            Assert.Equal(7, session.DroppedCount);
            Assert.Equal(["msg-8", "msg-9", "msg-10"], session.ReceivedMessages);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Sse_Connect_ParsesMultiLineDataAndEvents()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.MapGet("/events", async (HttpContext context) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";

            string ssePayload =
                ": this is a ping comment\n" +
                "id: evt-101\n" +
                "event: user_join\n" +
                "data: {\"userId\": 123,\n" +
                "data: \"role\": \"admin\"}\n" +
                "retry: 5000\n\n" +
                "id: evt-102\n" +
                "event: notification\n" +
                "data: Hello world!\n\n";

            await context.Response.WriteAsync(ssePayload);
            await context.Response.Body.FlushAsync();
        });

        await app.StartAsync();

        try
        {
            string httpUrl = app.Urls.First();
            var sseUri = new Uri(httpUrl + "/events");

            using var httpClient = new HttpClient();
            await using var session = new SseSession(httpClient);
            await session.ConnectAsync(sseUri);

            var firstEvent = await session.AwaitEventAsync(e => e.Id == "evt-101", TimeSpan.FromSeconds(5));
            Assert.Equal("evt-101", firstEvent.Id);
            Assert.Equal("user_join", firstEvent.EventName);
            Assert.Equal("{\"userId\": 123,\n\"role\": \"admin\"}", firstEvent.Data);
            Assert.Equal(5000, firstEvent.RetryMs);

            var secondEvent = await session.AwaitEventAsync(e => e.Id == "evt-102", TimeSpan.FromSeconds(5));
            Assert.Equal("evt-102", secondEvent.Id);
            Assert.Equal("notification", secondEvent.EventName);
            Assert.Equal("Hello world!", secondEvent.Data);

            Assert.Equal("evt-102", session.LastEventId);
            Assert.Equal(2, session.ReceivedEvents.Count);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Sse_BoundedRetention_DropsOldestEvents()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();

        app.MapGet("/stream-many", async (HttpContext context) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            for (int i = 1; i <= 10; i++)
            {
                await context.Response.WriteAsync($"id: id-{i}\nevent: tick\ndata: value-{i}\n\n");
                await context.Response.Body.FlushAsync();
            }
        });

        await app.StartAsync();

        try
        {
            string httpUrl = app.Urls.First();
            var sseUri = new Uri(httpUrl + "/stream-many");

            using var httpClient = new HttpClient();
            var limits = new StreamingLimits(MaxMessages: 4);
            await using var session = new SseSession(httpClient, limits);
            await session.ConnectAsync(sseUri);

            var lastEvent = await session.AwaitEventAsync(e => e.Id == "id-10", TimeSpan.FromSeconds(5));
            Assert.Equal("id-10", lastEvent.Id);

            Assert.Equal(4, session.ReceivedEvents.Count);
            Assert.Equal(6, session.DroppedCount);
            Assert.Equal("id-10", session.LastEventId);
            Assert.Equal(["id-7", "id-8", "id-9", "id-10"], session.ReceivedEvents.Select(e => e.Id));
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}

