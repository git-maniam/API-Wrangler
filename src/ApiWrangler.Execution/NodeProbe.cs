using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace ApiWrangler.Execution;

public sealed record NodeProbeResult(int Version, string Id, string Value, int? ChildPid);

public static class NodeProbe
{
    public static async Task<NodeProbeResult> RunAsync(string nodeExecutable, string workerPath,
        int delayMs = 0, string? childPidFile = null, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var start = new ProcessStartInfo(nodeExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.GetFullPath(workerPath));
        using var process = Process.Start(start) ?? throw new IOException("Node worker could not start.");
        var stderr = DrainAsync(process.StandardError, deadline.Token);
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var request = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, id, delayMs, childPidFile });
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(prefix, request.Length);
            await process.StandardInput.BaseStream.WriteAsync(prefix, deadline.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(request, deadline.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(deadline.Token).ConfigureAwait(false);
            await process.StandardOutput.BaseStream.ReadExactlyAsync(prefix, deadline.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
            if (length is < 1 or > 65_536) throw new IOException("Invalid worker frame size.");
            var bytes = new byte[length];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(bytes, deadline.Token).ConfigureAwait(false);
            var result = JsonSerializer.Deserialize<NodeProbeResult>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new IOException("Empty worker response.");
            if (result.Version != 1 || result.Id != id) throw new IOException("Worker response correlation failed.");
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Worker exited unsuccessfully.");
            return result;
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await deadline.CancelAsync().ConfigureAwait(false);
            try { await stderr.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken token)
    {
        // Discard diagnostics rather than retaining arbitrary or sensitive worker output.
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, token).ConfigureAwait(false) != 0) { }
    }
}
