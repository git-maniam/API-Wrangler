using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Execution;
using ApiWrangler.Storage;

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
json.Converters.Add(new JsonStringEnumConverter());
var root = new RootCommand("API Wrangler foundation preview — explicit HTTP and feasibility commands");

var send = new Command("send", "Send one HTTP request. Response bodies are stored locally; redirects are not followed.");
var url = new Option<string>("--url") { Required = true, Description = "Absolute HTTP(S) URL" };
var method = new Option<string>("--method") { DefaultValueFactory = _ => "GET" };
var bodyFile = new Option<FileInfo?>("--body-file") { Description = "Read a UTF-8 JSON body from this file" };
var timeout = new Option<int>("--timeout-ms") { DefaultValueFactory = _ => 30_000 };
var output = new Option<DirectoryInfo>("--output")
{
    DefaultValueFactory = _ => new DirectoryInfo(Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApiWrangler", "runs"))
};
foreach (var option in new Option[] { url, method, bodyFile, timeout, output }) send.Options.Add(option);
send.SetAction(async (parse, token) =>
{
    try
    {
        var body = parse.GetValue(bodyFile);
        using var transport = new HttpTransport(parse.GetValue(output)!.FullName);
        var result = await new HttpRequestService(transport).SendAsync(new HttpRequestDefinition
        {
            Url = parse.GetValue(url)!,
            Method = parse.GetValue(method)!,
            TimeoutMs = parse.GetValue(timeout),
            Body = body is null ? null : await File.ReadAllTextAsync(body.FullName, token)
        }, cancellationToken: token);
        // Keep raw response contents out of stdout; the explicit local artifact contains the bytes.
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result.Outcome,
            result.StatusCode,
            result.BytesReceived,
            result.PreviewTruncated,
            result.BodyPath,
            durationMs = result.Duration.TotalMilliseconds,
            result.Error
        }, json));
        return result.Outcome switch { ExecutionOutcome.Passed => 0, ExecutionOutcome.Cancelled => 4, _ => 3 };
    }
    catch (ArgumentException) { Console.Error.WriteLine("Invalid URL, method, headers, or response limits."); return 2; }
    catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 4; }
    catch (IOException) { Console.Error.WriteLine("Unable to read the input or write the response artifact."); return 3; }
});
root.Subcommands.Add(send);

var git = new Command("git-probe", "Inspect or explicitly fetch using the installed Git executable and configured authentication.");
var repository = new Option<DirectoryInfo>("--repository") { Required = true };
var fetch = new Option<bool>("--fetch") { Description = "Fetch origin without integrating working files" };
git.Options.Add(repository);
git.Options.Add(fetch);
git.SetAction(async (parse, token) =>
{
    try
    {
        if (!Directory.Exists(parse.GetValue(repository)!.FullName))
        {
            Console.Error.WriteLine("Repository directory does not exist.");
            return 2;
        }
        var probe = new GitProbe();
        var result = parse.GetValue(fetch)
            ? await probe.FetchAsync(parse.GetValue(repository)!.FullName, token)
            : await probe.StatusAsync(parse.GetValue(repository)!.FullName, token);
        // Git diagnostics may contain remote URLs; retain only exit status in machine-readable output.
        Console.WriteLine(JsonSerializer.Serialize(new { result.ExitCode, success = result.ExitCode == 0 }, json));
        return result.ExitCode == 0 ? 0 : 3;
    }
    catch (System.ComponentModel.Win32Exception) { Console.Error.WriteLine("Git executable unavailable."); return 5; }
    catch (OperationCanceledException) { Console.Error.WriteLine("Git operation cancelled."); return 4; }
    catch (IOException) { Console.Error.WriteLine("Git operation failed."); return 3; }
});
root.Subcommands.Add(git);

var node = new Command("node-probe", "Explicitly run the bundled feasibility probe; does not execute project scripts.");
var executable = new Option<string>("--node") { DefaultValueFactory = _ => "node" };
var worker = new Option<FileInfo>("--worker") { Required = true };
node.Options.Add(executable);
node.Options.Add(worker);
node.SetAction(async (parse, token) =>
{
    try
    {
        var result = await NodeProbe.RunAsync(parse.GetValue(executable)!, parse.GetValue(worker)!.FullName, cancellationToken: token);
        Console.WriteLine(JsonSerializer.Serialize(result, json));
        return 0;
    }
    catch (System.ComponentModel.Win32Exception) { Console.Error.WriteLine("Configured Node executable unavailable."); return 5; }
    catch (OperationCanceledException) { Console.Error.WriteLine("Worker cancelled or deadline exceeded."); return token.IsCancellationRequested ? 4 : 3; }
    catch (Exception exception) when (exception is IOException or JsonException)
    { Console.Error.WriteLine("Worker failed its protocol or exited unexpectedly."); return 3; }
});
root.Subcommands.Add(node);

var run = new Command("run", "Execute an API Wrangler test collection with lifecycle, isolated iterations, and flow control.");
var runProj = new Option<DirectoryInfo>("--project") { DefaultValueFactory = _ => new DirectoryInfo(Directory.GetCurrentDirectory()), Description = "Project directory containing wrangler.json" };
var runColl = new Option<string>("--collection") { Required = true, Description = "Collection name or ID to execute" };
var runEnv = new Option<string?>("--environment") { Description = "Environment name or ID to apply" };
var runIter = new Option<int>("--iterations") { DefaultValueFactory = _ => 1, Description = "Number of iterations to execute" };
var runConcurrency = new Option<int>("--concurrency") { DefaultValueFactory = _ => 1, Description = "Maximum concurrent iterations" };
var runStopOnFail = new Option<bool>("--stop-on-failure") { DefaultValueFactory = _ => false, Description = "Stop executing requests upon first failure" };
var runDelay = new Option<int>("--delay-ms") { DefaultValueFactory = _ => 0, Description = "Delay in milliseconds between requests" };
var runTeardown = new Option<string>("--teardown") { DefaultValueFactory = _ => "Always", Description = "Teardown policy: Always, OnFailure, OnSuccess" };
var runArtifacts = new Option<DirectoryInfo>("--output")
{
    DefaultValueFactory = _ => new DirectoryInfo(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApiWrangler", "runs"))
};

foreach (var opt in new Option[] { runProj, runColl, runEnv, runIter, runConcurrency, runStopOnFail, runDelay, runTeardown, runArtifacts })
    run.Options.Add(opt);

run.SetAction(async (parse, token) =>
{
    try
    {
        var projDir = parse.GetValue(runProj)!.FullName;
        var repo = new ProjectRepository();
        await using var session = await repo.OpenAsync(projDir, writable: false, token: token);
        var view = await session.ReadAsync(token);
        if (!view.IsValid)
        {
            Console.Error.WriteLine("Project validation failed.");
            foreach (var diag in view.Diagnostics)
                Console.Error.WriteLine($" - {diag.File}: {diag.Message}");
            return 2;
        }

        var collParam = parse.GetValue(runColl)!;
        var collAsset = view.Assets.FirstOrDefault(a => a.Kind == AssetKind.Collection &&
            (string.Equals(a.Name, collParam, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(a.Id, collParam, StringComparison.OrdinalIgnoreCase)));

        if (collAsset is null || !Guid.TryParse(collAsset.Id, out var collectionId))
        {
            Console.Error.WriteLine($"Collection '{collParam}' not found in project.");
            return 2;
        }

        var baseContext = new VariableContext();
        var envParam = parse.GetValue(runEnv);
        if (!string.IsNullOrEmpty(envParam))
        {
            var envAsset = view.Assets.FirstOrDefault(a => a.Kind == AssetKind.Environment &&
                (string.Equals(a.Name, envParam, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(a.Id, envParam, StringComparison.OrdinalIgnoreCase)));
            if (envAsset?.Content["variables"] is JsonObject envVars)
            {
                foreach (var kvp in envVars)
                {
                    if (kvp.Value is JsonValue jv) baseContext.Environment[kvp.Key] = jv.ToString();
                }
            }
        }

        var teardownStr = parse.GetValue(runTeardown) ?? "Always";
        var teardownPolicy = Enum.TryParse<TeardownPolicy>(teardownStr, true, out var tp) ? tp : TeardownPolicy.Always;

        var options = new CollectionExecutionOptions
        {
            Iterations = parse.GetValue(runIter),
            Concurrency = parse.GetValue(runConcurrency),
            StopOnFailure = parse.GetValue(runStopOnFail),
            DelayBetweenRequestsMs = parse.GetValue(runDelay),
            TeardownPolicy = teardownPolicy
        };

        using var transport = new HttpTransport(parse.GetValue(runArtifacts)!.FullName);
        var httpService = new HttpRequestService(transport);
        var runner = new CollectionRunner(httpService);

        var report = await runner.ExecuteCollectionAsync(view, collectionId, options, baseContext, token);
        Console.WriteLine(JsonSerializer.Serialize(report, json));

        return report.Status switch
        {
            RunStatus.Passed => 0,
            RunStatus.Cancelled => 4,
            _ => 3
        };
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Run cancelled."); return 4; }
    catch (Exception ex) { Console.Error.WriteLine($"Run error: {ex.Message}"); return 3; }
});
root.Subcommands.Add(run);

return await root.Parse(args).InvokeAsync();
