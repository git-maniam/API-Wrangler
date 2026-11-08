using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using ApiWrangler.Application;

namespace ApiWrangler.Execution;

public enum ScriptOutcome
{
    Passed,
    Failed,
    Error,
    TimedOut,
    Cancelled
}

public sealed record ScriptTestResult(
    string Name,
    bool Passed,
    string? Error
);

public sealed record ScriptVariableMutation(
    string Scope,
    string Key,
    JsonElement? Value,
    string Action
);

public sealed record ScriptConsoleLog(
    string Level,
    string Message
);

public sealed record ScriptRequestContext(
    string Url,
    string Method,
    IReadOnlyDictionary<string, string>? Headers = null,
    string? Body = null
);

public sealed record ScriptResponseContext(
    int Status,
    string StatusText,
    IReadOnlyDictionary<string, string>? Headers,
    string Body,
    long ResponseTimeMs,
    long SizeBytes
);

public sealed record ScriptExecutionRequest(
    string Code,
    string Stage = "test",
    string? ProjectPath = null,
    TimeSpan? Timeout = null,
    ScriptRequestContext? Request = null,
    ScriptResponseContext? Response = null,
    IReadOnlyDictionary<string, object?>? Variables = null,
    IReadOnlyDictionary<string, object?>? Environment = null,
    IReadOnlyDictionary<string, object?>? CollectionVariables = null,
    IReadOnlyDictionary<string, object?>? IterationData = null
);

public sealed record ScriptExecutionResult(
    bool Success,
    ScriptOutcome Outcome,
    IReadOnlyList<ScriptTestResult> TestResults,
    IReadOnlyList<ScriptVariableMutation> VariableMutations,
    IReadOnlyList<ScriptConsoleLog> ConsoleLogs,
    string? NextRequest,
    string? Error,
    TimeSpan Duration
);

public sealed class NodeScriptHost
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NodeRuntimeService runtimeService;
    private readonly ProjectTrustService trustService;
    private readonly string? customWorkerPath;

    public NodeScriptHost(
        NodeRuntimeService? runtime = null,
        ProjectTrustService? trust = null,
        string? workerPath = null)
    {
        runtimeService = runtime ?? new NodeRuntimeService();
        trustService = trust ?? new ProjectTrustService();
        customWorkerPath = workerPath;
    }

    public async Task<ScriptExecutionResult> ExecuteAsync(
        ScriptExecutionRequest request,
        CancellationToken token = default)
    {
        if (request.ProjectPath != null)
        {
            await trustService.RequireTrustAsync(request.ProjectPath, token).ConfigureAwait(false);
        }

        NodeRuntimeInfo runtime = await runtimeService.DiscoverAsync(token: token).ConfigureAwait(false);
        if (!runtime.IsAvailable || string.IsNullOrEmpty(runtime.NodePath))
        {
            throw new InvalidOperationException($"Cannot execute script: {runtime.Diagnostics}");
        }

        string workerPath = ResolveWorkerPath();
        if (!File.Exists(workerPath))
        {
            throw new FileNotFoundException($"Node worker script not found at '{workerPath}'. Build the worker in workers/node.", workerPath);
        }

        TimeSpan timeout = request.Timeout ?? TimeSpan.FromSeconds(5);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        linkedCts.CancelAfter(timeout);

        var psi = new ProcessStartInfo(runtime.NodePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add(workerPath);

        if (request.ProjectPath != null)
        {
            psi.WorkingDirectory = request.ProjectPath;
        }

        var sw = Stopwatch.StartNew();
        using Process process = Process.Start(psi) ?? throw new IOException("Failed to start Node worker process.");

        try
        {
            string requestId = Guid.NewGuid().ToString("N");
            var payload = new
            {
                version = 1,
                id = requestId,
                action = "execute",
                code = request.Code,
                stage = request.Stage,
                projectPath = request.ProjectPath,
                timeoutMs = (int)timeout.TotalMilliseconds,
                request = request.Request,
                response = request.Response,
                variables = request.Variables,
                environment = request.Environment,
                collectionVariables = request.CollectionVariables,
                iterationData = request.IterationData
            };

            byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
            byte[] lengthPrefix = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, jsonBytes.Length);

            await process.StandardInput.BaseStream.WriteAsync(lengthPrefix, linkedCts.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(jsonBytes, linkedCts.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(linkedCts.Token).ConfigureAwait(false);

            // Read response frame
            byte[] responsePrefix = new byte[4];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(responsePrefix, linkedCts.Token).ConfigureAwait(false);
            int responseLength = BinaryPrimitives.ReadInt32BigEndian(responsePrefix);

            if (responseLength is < 1 or > 16 * 1024 * 1024)
            {
                throw new IOException($"Invalid worker response frame length: {responseLength}");
            }

            byte[] responseBytes = new byte[responseLength];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(responseBytes, linkedCts.Token).ConfigureAwait(false);

            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            sw.Stop();

            using var doc = JsonDocument.Parse(responseBytes);
            JsonElement root = doc.RootElement;

            bool success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            string outcomeStr = root.TryGetProperty("outcome", out var o) ? (o.GetString() ?? "Error") : "Error";
            ScriptOutcome outcome = Enum.TryParse<ScriptOutcome>(outcomeStr, true, out var parsedOutcome)
                ? parsedOutcome
                : (success ? ScriptOutcome.Passed : ScriptOutcome.Error);

            string? error = root.TryGetProperty("error", out var err) ? err.GetString() : null;

            var testResults = new List<ScriptTestResult>();
            if (root.TryGetProperty("testResults", out var tr) && tr.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in tr.EnumerateArray())
                {
                    string name = item.TryGetProperty("name", out var tn) ? (tn.GetString() ?? "") : "";
                    bool passed = item.TryGetProperty("passed", out var tp) && tp.GetBoolean();
                    string? testErr = item.TryGetProperty("error", out var te) ? te.GetString() : null;
                    testResults.Add(new ScriptTestResult(name, passed, testErr));
                }
            }

            var mutations = new List<ScriptVariableMutation>();
            if (root.TryGetProperty("variableMutations", out var vm) && vm.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in vm.EnumerateArray())
                {
                    string scope = item.TryGetProperty("scope", out var sc) ? (sc.GetString() ?? "variables") : "variables";
                    string key = item.TryGetProperty("key", out var k) ? (k.GetString() ?? "") : "";
                    string action = item.TryGetProperty("action", out var a) ? (a.GetString() ?? "set") : "set";
                    JsonElement? val = item.TryGetProperty("value", out var v) ? v.Clone() : null;
                    mutations.Add(new ScriptVariableMutation(scope, key, val, action));
                }
            }

            var consoleLogs = new List<ScriptConsoleLog>();
            if (root.TryGetProperty("consoleLogs", out var cl) && cl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in cl.EnumerateArray())
                {
                    string level = item.TryGetProperty("level", out var l) ? (l.GetString() ?? "log") : "log";
                    string message = item.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "";
                    consoleLogs.Add(new ScriptConsoleLog(level, message));
                }
            }

            string? nextRequest = root.TryGetProperty("nextRequest", out var nr) && nr.ValueKind == JsonValueKind.String ? nr.GetString() : null;

            return new ScriptExecutionResult(
                Success: success && outcome == ScriptOutcome.Passed,
                Outcome: outcome,
                TestResults: testResults,
                VariableMutations: mutations,
                ConsoleLogs: consoleLogs,
                NextRequest: nextRequest,
                Error: error,
                Duration: sw.Elapsed
            );
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !token.IsCancellationRequested)
        {
            // Wall-clock timeout exceeded
            KillProcessTree(process);
            return new ScriptExecutionResult(
                Success: false,
                Outcome: ScriptOutcome.TimedOut,
                TestResults: Array.Empty<ScriptTestResult>(),
                VariableMutations: Array.Empty<ScriptVariableMutation>(),
                ConsoleLogs: Array.Empty<ScriptConsoleLog>(),
                NextRequest: null,
                Error: $"Script execution timed out after {timeout.TotalSeconds:F1} seconds. The worker process tree was terminated.",
                Duration: sw.Elapsed
            );
        }
        catch (OperationCanceledException)
        {
            // Explicit cancellation requested by caller
            KillProcessTree(process);
            return new ScriptExecutionResult(
                Success: false,
                Outcome: ScriptOutcome.Cancelled,
                TestResults: Array.Empty<ScriptTestResult>(),
                VariableMutations: Array.Empty<ScriptVariableMutation>(),
                ConsoleLogs: Array.Empty<ScriptConsoleLog>(),
                NextRequest: null,
                Error: "Script execution was cancelled.",
                Duration: sw.Elapsed
            );
        }
        finally
        {
            if (!process.HasExited)
            {
                KillProcessTree(process);
            }
        }
    }

    private string ResolveWorkerPath()
    {
        if (!string.IsNullOrEmpty(customWorkerPath) && File.Exists(customWorkerPath))
            return Path.GetFullPath(customWorkerPath);

        string? envScriptWorker = Environment.GetEnvironmentVariable("AWR_NODE_SCRIPT_WORKER");
        if (!string.IsNullOrEmpty(envScriptWorker) && File.Exists(envScriptWorker))
            return Path.GetFullPath(envScriptWorker);

        string? envWorker = Environment.GetEnvironmentVariable("AWR_NODE_WORKER");
        if (!string.IsNullOrEmpty(envWorker) && File.Exists(envWorker) && Path.GetFileName(envWorker).Equals("worker.js", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(envWorker);

        string? current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            string candidate = Path.Combine(current, "workers", "node", "dist", "worker.js");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            current = Path.GetDirectoryName(current);
        }

        current = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(current))
        {
            string candidate = Path.Combine(current, "workers", "node", "dist", "worker.js");
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            current = Path.GetDirectoryName(current);
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "workers", "node", "dist", "worker.js");
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch
        {
            // Ignored if already terminated
        }
    }
}

