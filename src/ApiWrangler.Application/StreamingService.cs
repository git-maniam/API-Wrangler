using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using ApiWrangler.Domain;

namespace ApiWrangler.Application;

public sealed class WebSocketSession : IAsyncDisposable
{
    private readonly ClientWebSocket client = new();
    private readonly List<StreamingTranscriptEntry> transcript = new();
    private readonly List<string> receivedMessages = new();
    private readonly List<(Func<string, bool> Predicate, TaskCompletionSource<string> Tcs)> waiters = new();
    private readonly object syncRoot = new();
    private readonly StreamingLimits limits;
    private readonly StreamingReconnectionPolicy reconnectPolicy;
    private CancellationTokenSource? receiveLoopCts;
    private Task? receiveTask;
    private int droppedCount = 0;

    public IReadOnlyList<StreamingTranscriptEntry> Transcript
    {
        get { lock (syncRoot) return transcript.ToList(); }
    }

    public IReadOnlyList<string> ReceivedMessages
    {
        get { lock (syncRoot) return receivedMessages.ToList(); }
    }

    public int DroppedCount
    {
        get { lock (syncRoot) return droppedCount; }
    }

    public WebSocketState State => client.State;

    public WebSocketSession(
        StreamingLimits? limits = null,
        StreamingReconnectionPolicy? reconnectPolicy = null)
    {
        this.limits = limits ?? new StreamingLimits();
        this.reconnectPolicy = reconnectPolicy ?? StreamingReconnectionPolicy.Disabled;
    }

    public async Task ConnectAsync(
        Uri uri,
        IReadOnlyDictionary<string, string>? headers = null,
        IReadOnlyList<string>? subprotocols = null,
        TimeSpan? timeout = null,
        CancellationToken token = default)
    {
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                client.Options.SetRequestHeader(k, v);
            }
        }

        if (subprotocols is not null)
        {
            foreach (var sp in subprotocols)
            {
                client.Options.AddSubProtocol(sp);
            }
        }

        RecordEvent(StreamDirection.System, StreamEventType.Connecting, $"Connecting to {uri}...");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (timeout.HasValue)
        {
            linkedCts.CancelAfter(timeout.Value);
        }

        try
        {
            await client.ConnectAsync(uri, linkedCts.Token).ConfigureAwait(false);
            RecordEvent(StreamDirection.System, StreamEventType.Connected, $"Connected to {uri}. Subprotocol: '{client.SubProtocol}'");
        }
        catch (Exception ex)
        {
            RecordEvent(StreamDirection.System, StreamEventType.Error, $"Connection failed: {ex.Message}");
            throw;
        }

        receiveLoopCts = new CancellationTokenSource();
        receiveTask = Task.Run(() => ReceiveLoopAsync(receiveLoopCts.Token));
    }

    public async Task SendMessageAsync(string message, CancellationToken token = default)
    {
        if (client.State != WebSocketState.Open)
        {
            throw new InvalidOperationException($"Cannot send message; WebSocket state is {client.State}.");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(message);
        await client.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        RecordEvent(StreamDirection.Outbound, StreamEventType.MessageSent, message, bytes.Length);
    }

    public async Task<string> AwaitMessageAsync(
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken token = default)
    {
        // 1. Check existing buffer
        lock (syncRoot)
        {
            var match = receivedMessages.FirstOrDefault(predicate);
            if (match is not null) return match;
        }

        // 2. Register waiter
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (syncRoot)
        {
            waiters.Add((predicate, tcs));
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        linkedCts.CancelAfter(timeout);

        using (linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token)))
        {
            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (syncRoot)
                {
                    waiters.RemoveAll(w => w.Tcs == tcs);
                }
            }
        }
    }

    public async Task DisconnectAsync(
        WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
        string description = "Client closed session.",
        CancellationToken token = default)
    {
        if (client.State == WebSocketState.Open)
        {
            try
            {
                await client.CloseAsync(status, description, token).ConfigureAwait(false);
            }
            catch
            {
                // Ignored if already closing
            }
        }

        RecordEvent(StreamDirection.System, StreamEventType.Disconnected, $"Disconnected ({status}: {description})");
        receiveLoopCts?.Cancel();
        if (receiveTask is not null)
        {
            try { await receiveTask.ConfigureAwait(false); } catch { }
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[8192];
        var messageBuilder = new StringBuilder();

        while (!token.IsCancellationRequested && client.State == WebSocketState.Open)
        {
            try
            {
                WebSocketReceiveResult result = await client.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    RecordEvent(StreamDirection.Inbound, StreamEventType.Disconnected, $"Server requested close: {result.CloseStatus} ({result.CloseStatusDescription})");
                    break;
                }

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    string fullMessage = messageBuilder.ToString();
                    messageBuilder.Clear();

                    lock (syncRoot)
                    {
                        if (receivedMessages.Count >= limits.MaxMessages)
                        {
                            receivedMessages.RemoveAt(0);
                            droppedCount++;
                        }
                        receivedMessages.Add(fullMessage);

                        // Check waiters matching predicate
                        for (int i = waiters.Count - 1; i >= 0; i--)
                        {
                            if (waiters[i].Predicate(fullMessage))
                            {
                                waiters[i].Tcs.TrySetResult(fullMessage);
                                waiters.RemoveAt(i);
                            }
                        }
                    }

                    RecordEvent(StreamDirection.Inbound, StreamEventType.MessageReceived, fullMessage, Encoding.UTF8.GetByteCount(fullMessage));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                RecordEvent(StreamDirection.System, StreamEventType.Error, $"Receive error: {ex.Message}");
                break;
            }
        }
    }

    private void RecordEvent(StreamDirection dir, StreamEventType type, string? payload = null, long size = 0)
    {
        lock (syncRoot)
        {
            transcript.Add(new StreamingTranscriptEntry(DateTimeOffset.UtcNow, dir, type, payload, size));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        receiveLoopCts?.Dispose();
        client.Dispose();
    }
}

public sealed class SseSession : IAsyncDisposable
{
    private readonly HttpClient httpClient;
    private readonly List<StreamingTranscriptEntry> transcript = new();
    private readonly List<SseEventEntry> receivedEvents = new();
    private readonly List<(Func<SseEventEntry, bool> Predicate, TaskCompletionSource<SseEventEntry> Tcs)> waiters = new();
    private readonly object syncRoot = new();
    private readonly StreamingLimits limits;
    private CancellationTokenSource? streamCts;
    private Task? streamTask;
    private string? lastEventId;
    private int droppedCount = 0;

    public IReadOnlyList<StreamingTranscriptEntry> Transcript
    {
        get { lock (syncRoot) return transcript.ToList(); }
    }

    public IReadOnlyList<SseEventEntry> ReceivedEvents
    {
        get { lock (syncRoot) return receivedEvents.ToList(); }
    }

    public int DroppedCount
    {
        get { lock (syncRoot) return droppedCount; }
    }

    public string? LastEventId => lastEventId;

    public SseSession(HttpClient? client = null, StreamingLimits? limits = null)
    {
        this.httpClient = client ?? new HttpClient();
        this.limits = limits ?? new StreamingLimits();
    }

    public async Task ConnectAsync(
        Uri uri,
        IReadOnlyDictionary<string, string>? headers = null,
        TimeSpan? timeout = null,
        CancellationToken token = default)
    {
        streamCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, streamCts.Token);
        if (timeout.HasValue)
        {
            linkedCts.CancelAfter(timeout.Value);
        }

        RecordEvent(StreamDirection.System, StreamEventType.Connecting, $"Connecting to SSE stream at {uri}...");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        if (!string.IsNullOrEmpty(lastEventId))
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        }

        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                request.Headers.TryAddWithoutValidation(k, v);
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            RecordEvent(StreamDirection.System, StreamEventType.Connected, $"Connected to SSE stream (HTTP {(int)response.StatusCode})");
        }
        catch (Exception ex)
        {
            RecordEvent(StreamDirection.System, StreamEventType.Error, $"SSE connection failed: {ex.Message}");
            throw;
        }

        Stream stream = await response.Content.ReadAsStreamAsync(linkedCts.Token).ConfigureAwait(false);
        streamTask = Task.Run(() => ReadStreamAsync(stream, response, streamCts.Token));
    }

    public async Task<SseEventEntry> AwaitEventAsync(
        Func<SseEventEntry, bool> predicate,
        TimeSpan timeout,
        CancellationToken token = default)
    {
        lock (syncRoot)
        {
            var match = receivedEvents.FirstOrDefault(predicate);
            if (match is not null) return match;
        }

        var tcs = new TaskCompletionSource<SseEventEntry>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (syncRoot)
        {
            waiters.Add((predicate, tcs));
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        linkedCts.CancelAfter(timeout);

        using (linkedCts.Token.Register(() => tcs.TrySetCanceled(linkedCts.Token)))
        {
            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            finally
            {
                lock (syncRoot)
                {
                    waiters.RemoveAll(w => w.Tcs == tcs);
                }
            }
        }
    }

    public async Task DisconnectAsync()
    {
        RecordEvent(StreamDirection.System, StreamEventType.Disconnected, "SSE stream closed by client.");
        streamCts?.Cancel();
        if (streamTask is not null)
        {
            try { await streamTask.ConfigureAwait(false); } catch { }
        }
    }

    private async Task ReadStreamAsync(Stream stream, HttpResponseMessage response, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string currentEventName = "message";
        string? currentId = null;
        var dataLines = new List<string>();
        int? currentRetry = null;

        try
        {
            while (!token.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null) break; // Stream ended

                if (string.IsNullOrWhiteSpace(line))
                {
                    // Dispatch accumulated event
                    if (dataLines.Count > 0)
                    {
                        string data = string.Join("\n", dataLines);
                        var sseEntry = new SseEventEntry(currentId, currentEventName, data, currentRetry);

                        if (!string.IsNullOrEmpty(currentId))
                        {
                            lastEventId = currentId;
                        }

                        lock (syncRoot)
                        {
                            if (receivedEvents.Count >= limits.MaxMessages)
                            {
                                receivedEvents.RemoveAt(0);
                                droppedCount++;
                            }
                            receivedEvents.Add(sseEntry);

                            for (int i = waiters.Count - 1; i >= 0; i--)
                            {
                                if (waiters[i].Predicate(sseEntry))
                                {
                                    waiters[i].Tcs.TrySetResult(sseEntry);
                                    waiters.RemoveAt(i);
                                }
                            }
                        }

                        RecordEvent(StreamDirection.Inbound, StreamEventType.MessageReceived, $"event:{currentEventName} data:{data}", Encoding.UTF8.GetByteCount(data));

                        dataLines.Clear();
                        currentEventName = "message";
                        currentId = null;
                        currentRetry = null;
                    }
                    continue;
                }

                if (line.StartsWith(':')) continue; // Comment line

                int colonIndex = line.IndexOf(':');
                string field = colonIndex > 0 ? line[..colonIndex].Trim() : line.Trim();
                string value = colonIndex > 0 ? line[(colonIndex + 1)..].TrimStart() : "";

                switch (field)
                {
                    case "event":
                        currentEventName = value;
                        break;
                    case "data":
                        dataLines.Add(value);
                        break;
                    case "id":
                        currentId = value;
                        break;
                    case "retry":
                        if (int.TryParse(value, out int r)) currentRetry = r;
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            RecordEvent(StreamDirection.System, StreamEventType.Error, $"Stream reading error: {ex.Message}");
        }
        finally
        {
            response.Dispose();
        }
    }

    private void RecordEvent(StreamDirection dir, StreamEventType type, string? payload = null, long size = 0)
    {
        lock (syncRoot)
        {
            transcript.Add(new StreamingTranscriptEntry(DateTimeOffset.UtcNow, dir, type, payload, size));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        streamCts?.Dispose();
    }
}
