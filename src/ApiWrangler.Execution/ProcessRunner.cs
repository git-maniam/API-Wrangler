using System.Diagnostics;
using System.Text;

namespace ApiWrangler.Execution;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public static class ProcessRunner
{
    public const int DefaultMaxOutputCharacters = 65_536;

    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments,
        string workingDirectory, CancellationToken cancellationToken = default,
        int maxOutputCharacters = DefaultMaxOutputCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputCharacters);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Process could not be started.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var output = ReadBoundedAsync(process.StandardOutput, lifetime, maxOutputCharacters);
        var error = ReadBoundedAsync(process.StandardError, lifetime, maxOutputCharacters);
        try
        {
            await process.WaitForExitAsync(lifetime.Token).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"Process output exceeded the {maxOutputCharacters:N0}-character limit.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await lifetime.CancelAsync().ConfigureAwait(false);
            try { await Task.WhenAll(output, error).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationTokenSource lifetime,
        int maxOutputCharacters)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false)) > 0)
        {
            if (text.Length + count > maxOutputCharacters)
            {
                await lifetime.CancelAsync().ConfigureAwait(false);
                throw new IOException($"Process output exceeded the {maxOutputCharacters:N0}-character limit.");
            }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
