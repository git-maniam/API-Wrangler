using System.Diagnostics;
using ApiWrangler.Execution;

namespace ApiWrangler.IntegrationTests;

public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWR_NODE")) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AWR_NODE_WORKER")))
            Skip = "Set AWR_NODE and AWR_NODE_WORKER after explicitly building the Node probe.";
    }
}

public sealed class NodeTests
{
    [NodeFact]
    public async Task Process_OutputFlood_IsBoundedAndTerminates()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<IOException>(() => ProcessRunner.RunAsync(Environment.GetEnvironmentVariable("AWR_NODE")!,
            ["-e", "setInterval(() => process.stdout.write('x'.repeat(65536)), 1)"], Path.GetTempPath(), timeout.Token));
    }

    [NodeFact]
    public async Task Process_Arguments_ArePassedLiterallyWithoutShellExpansion()
    {
        const string argument = "spaces café ; $(echo should-not-run) & |";
        var result = await ProcessRunner.RunAsync(Environment.GetEnvironmentVariable("AWR_NODE")!,
            ["-e", "process.stdout.write(process.argv[1])", argument], Path.GetTempPath());
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(argument, result.StandardOutput);
    }

    [NodeFact]
    public async Task Worker_FramedRequest_ReturnsCorrelatedResponse()
    {
        var result = await NodeProbe.RunAsync(Environment.GetEnvironmentVariable("AWR_NODE")!,
            Environment.GetEnvironmentVariable("AWR_NODE_WORKER")!);
        Assert.Equal(1, result.Version);
        Assert.Equal("ready", result.Value);
    }

    [NodeFact]
    public async Task Worker_Cancellation_TerminatesChildProcess()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"awr-child-{Guid.NewGuid():N}.pid");
        using var cancel = new CancellationTokenSource();
        var run = NodeProbe.RunAsync(Environment.GetEnvironmentVariable("AWR_NODE")!,
            Environment.GetEnvironmentVariable("AWR_NODE_WORKER")!, 60_000, pidFile, cancel.Token);
        int? pid = null;
        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(pidFile) && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(25);
            Assert.True(File.Exists(pidFile), "Worker did not start its child.");
            pid = int.Parse(await File.ReadAllTextAsync(pidFile));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            try
            {
                using var child = Process.GetProcessById(pid.Value);
                using var exitDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await child.WaitForExitAsync(exitDeadline.Token);
                Assert.True(child.HasExited);
            }
            catch (ArgumentException) { /* Already reaped by the operating system. */ }
        }
        finally
        {
            cancel.Cancel();
            try { await run; } catch (OperationCanceledException) { }
            File.Delete(pidFile);
        }
    }
}
