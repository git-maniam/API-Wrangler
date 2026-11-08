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
var runReporters = new Option<string>("--reporters") { DefaultValueFactory = _ => "console", Description = "Comma-separated reporters: console, json, junit, markdown, html" };
var runReportDir = new Option<DirectoryInfo?>("--report-dir") { Description = "Directory to save report artifacts (junit.xml, report.json, report.html, report.md)" };
var runArtifacts = new Option<DirectoryInfo>("--output")
{
    DefaultValueFactory = _ => new DirectoryInfo(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApiWrangler", "runs"))
};

foreach (var opt in new Option[] { runProj, runColl, runEnv, runIter, runConcurrency, runStopOnFail, runDelay, runTeardown, runReporters, runReportDir, runArtifacts })
    run.Options.Add(opt);

run.SetAction(async (parse, token) =>
{
    try
    {
        var projDir = parse.GetValue(runProj)!.FullName;
        if (!Directory.Exists(projDir) || !File.Exists(Path.Combine(projDir, "wrangler.json")))
        {
            Console.Error.WriteLine($"Project manifest not found at '{projDir}'.");
            return 2;
        }

        var repo = new ProjectRepository();
        await using var session = await repo.OpenAsync(projDir, writable: false, token: token);
        var snapshot = await session.SnapshotAsync(token);

        await using var snapSession = await repo.OpenAsync(snapshot.Directory, writable: false, token: token);
        var view = await snapSession.ReadAsync(token);
        if (!view.IsValid)
        {
            Console.Error.WriteLine("Project validation failed in snapshot.");
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

        var report = await runner.ExecuteCollectionAsync(view, collectionId, options, baseContext, snapshot, token);

        var reportersList = (parse.GetValue(runReporters) ?? "console")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(r => r.ToLowerInvariant())
            .ToList();

        var reportDir = parse.GetValue(runReportDir);
        if (reportDir is not null)
        {
            reportDir.Create();
            if (reportersList.Contains("json"))
                await File.WriteAllTextAsync(Path.Combine(reportDir.FullName, "report.json"), new JsonReporter().GenerateReport(report), token);
            if (reportersList.Contains("junit"))
                await File.WriteAllTextAsync(Path.Combine(reportDir.FullName, "junit.xml"), new JUnitReporter().GenerateReport(report), token);
            if (reportersList.Contains("html"))
                await File.WriteAllTextAsync(Path.Combine(reportDir.FullName, "report.html"), new HtmlReporter().GenerateReport(report), token);
            if (reportersList.Contains("markdown"))
                await File.WriteAllTextAsync(Path.Combine(reportDir.FullName, "report.md"), new MarkdownReporter().GenerateReport(report), token);
        }

        if (reportersList.Contains("console"))
        {
            Console.WriteLine(new ConsoleReporter().GenerateReport(report));
        }
        else if (reportersList.Contains("json") && reportDir is null)
        {
            Console.WriteLine(new JsonReporter().GenerateReport(report));
        }

        return report.Status switch
        {
            RunStatus.Passed => 0,
            RunStatus.Failed => 1,
            RunStatus.Cancelled => 4,
            _ => 3
        };
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Run cancelled."); return 4; }
    catch (IOException ex) { Console.Error.WriteLine($"Project/storage error: {ex.Message}"); return 2; }
    catch (Exception ex) { Console.Error.WriteLine($"Run error: {ex.Message}"); return 3; }
});
root.Subcommands.Add(run);

var init = new Command("init", "Initialize a new canonical API Wrangler project.");
var initProj = new Option<DirectoryInfo>("--project")
{
    DefaultValueFactory = _ => new DirectoryInfo(Directory.GetCurrentDirectory()),
    Description = "Directory where the project will be initialized"
};
var initName = new Option<string>("--name")
{
    DefaultValueFactory = _ => "API Wrangler Project",
    Description = "Human-readable project name"
};
init.Options.Add(initProj);
init.Options.Add(initName);
init.SetAction(async (parse, token) =>
{
    try
    {
        var targetDir = parse.GetValue(initProj)!.FullName;
        var name = parse.GetValue(initName)!;

        var repo = new ProjectRepository();
        await using var session = await repo.InitializeAsync(targetDir, name, token);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            initialized = true,
            project = targetDir,
            name
        }, json));

        return 0;
    }
    catch (IOException ex) when (ex.Message.Contains("already contains", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Project already exists at '{parse.GetValue(initProj)!.FullName}'.");
        return 2;
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 4; }
    catch (Exception ex) { Console.Error.WriteLine($"Init failed: {ex.Message}"); return 3; }
});
root.Subcommands.Add(init);

var validate = new Command("validate", "Validate an API Wrangler project directory for schema and reference integrity.");
var validateProj = new Option<DirectoryInfo>("--project")
{
    DefaultValueFactory = _ => new DirectoryInfo(Directory.GetCurrentDirectory()),
    Description = "Project directory containing wrangler.json"
};
validate.Options.Add(validateProj);
validate.SetAction(async (parse, token) =>
{
    try
    {
        var targetDir = parse.GetValue(validateProj)!.FullName;
        if (!File.Exists(Path.Combine(targetDir, "wrangler.json")))
        {
            Console.Error.WriteLine($"No wrangler.json manifest found in '{targetDir}'.");
            return 2;
        }

        var repo = new ProjectRepository();
        await using var session = await repo.OpenAsync(targetDir, writable: false, token: token);
        var view = await session.ReadAsync(token);

        var resultObj = new
        {
            valid = view.IsValid,
            project = targetDir,
            totalAssets = view.Assets.Count,
            collections = view.Assets.Count(a => a.Kind == AssetKind.Collection),
            requests = view.Assets.Count(a => a.Kind == AssetKind.Request),
            environments = view.Assets.Count(a => a.Kind == AssetKind.Environment),
            diagnostics = view.Diagnostics.Select(d => new { file = d.File, message = d.Message }).ToList()
        };

        Console.WriteLine(JsonSerializer.Serialize(resultObj, json));
        return view.IsValid ? 0 : 2;
    }
    catch (OperationCanceledException) { Console.Error.WriteLine("Cancelled."); return 4; }
    catch (Exception ex) { Console.Error.WriteLine($"Validation failed: {ex.Message}"); return 3; }
});
root.Subcommands.Add(validate);

var runner = new Command("runner", "Unattended runner operations: scoped token enrollment and exact-commit execution.");

var runnerEnroll = new Command("enroll", "Generate a cryptographically secure, scoped enrollment token.");
var enrollName = new Option<string>("--name") { Required = true, Description = "Descriptive name for the runner token" };
var enrollScope = new Option<string>("--scope") { DefaultValueFactory = _ => "RunnerRegistration", Description = "Token scope: RunnerRegistration, JobSubmission, Admin" };
var enrollDays = new Option<int>("--validity-days") { DefaultValueFactory = _ => 30, Description = "Validity period in days" };
runnerEnroll.Options.Add(enrollName);
runnerEnroll.Options.Add(enrollScope);
runnerEnroll.Options.Add(enrollDays);
runnerEnroll.SetAction((parse) =>
{
    var scope = Enum.TryParse<RunnerEnrollmentScope>(parse.GetValue(enrollScope), true, out var s) ? s : RunnerEnrollmentScope.RunnerRegistration;
    var svc = new RunnerEnrollmentService();
    var res = svc.CreateToken(parse.GetValue(enrollName)!, scope, TimeSpan.FromDays(parse.GetValue(enrollDays)));
    if (!res.Success)
    {
        Console.Error.WriteLine(res.ErrorMessage);
        return 2;
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        tokenId = res.TokenId,
        scope = scope.ToString(),
        token = res.PlaintextToken,
        warning = "Plaintext token will not be shown again. Store securely on the runner host."
    }, json));
    return 0;
});
runner.Subcommands.Add(runnerEnroll);

var runnerExec = new Command("exec", "Execute a job against an exact commit in an isolated runner directory.");
var execJobId = new Option<string>("--job-id") { Required = true, Description = "Unique Job ID" };
var execCommit = new Option<string>("--commit") { Required = true, Description = "Exact 40-character or 7+ character commit SHA" };
var execRepo = new Option<string>("--repo") { DefaultValueFactory = _ => ".", Description = "Repository URL or directory" };
var execOutput = new Option<DirectoryInfo>("--output")
{
    DefaultValueFactory = _ => new DirectoryInfo(Path.Combine(Path.GetTempPath(), "api-wrangler-runner")),
    Description = "Isolated execution working directory"
};
runnerExec.Options.Add(execJobId);
runnerExec.Options.Add(execCommit);
runnerExec.Options.Add(execRepo);
runnerExec.Options.Add(execOutput);
runnerExec.SetAction(async (parse, token) =>
{
    var job = new RunnerJobDefinition(
        JobId: parse.GetValue(execJobId)!,
        ProjectId: "cli-execution",
        RepositoryUrl: parse.GetValue(execRepo)!,
        ExactCommitSha: parse.GetValue(execCommit)!
    );

    var executor = new ExactCommitJobExecutor();
    var result = await executor.ExecuteJobAsync(job, parse.GetValue(execOutput)!.FullName, cancellationToken: token);
    Console.WriteLine(JsonSerializer.Serialize(result, json));
    return result.ExitCode;
});
runner.Subcommands.Add(runnerExec);

root.Subcommands.Add(runner);

var mock = new Command("mock", "Mock server operations: run local or shared mock instances with explicit authentication.");

var mockServe = new Command("serve", "Start a headless mock server instance from a JSON rules file.");
var rulesFile = new Option<FileInfo?>("--rules") { Description = "Path to JSON file containing MockRule array" };
var portOption = new Option<int>("--port") { DefaultValueFactory = _ => 0, Description = "Port number (0 for dynamic)" };
var hostOption = new Option<string>("--host") { DefaultValueFactory = _ => "127.0.0.1", Description = "Host address to bind to" };
var allowExternalOption = new Option<bool>("--allow-external") { Description = "Allow remote/external network binding" };
var authTokenOption = new Option<string?>("--auth-token") { Description = "Authentication token required for requests" };
var adminKeyOption = new Option<string?>("--admin-key") { Description = "Admin API key for state reset and logs" };
var maxLogsOption = new Option<int>("--max-logs") { DefaultValueFactory = _ => 1000, Description = "Maximum recorded log entries" };

mockServe.Options.Add(rulesFile);
mockServe.Options.Add(portOption);
mockServe.Options.Add(hostOption);
mockServe.Options.Add(allowExternalOption);
mockServe.Options.Add(authTokenOption);
mockServe.Options.Add(adminKeyOption);
mockServe.Options.Add(maxLogsOption);

mockServe.SetAction(async (parse, token) =>
{
    try
    {
        var file = parse.GetValue(rulesFile);
        var rules = new List<MockRule>();
        if (file is not null && file.Exists)
        {
            var content = await File.ReadAllTextAsync(file.FullName, token);
            var deserialized = JsonSerializer.Deserialize<List<MockRule>>(content, json);
            if (deserialized is not null)
            {
                rules.AddRange(deserialized);
            }
        }

        var config = new MockServerConfiguration(
            Host: parse.GetValue(hostOption)!,
            Port: parse.GetValue(portOption),
            AllowExternalBinding: parse.GetValue(allowExternalOption),
            AuthToken: parse.GetValue(authTokenOption),
            AdminApiKey: parse.GetValue(adminKeyOption),
            MaxLogRetention: parse.GetValue(maxLogsOption)
        );

        var engine = new MockEngine();
        foreach (var r in rules)
        {
            engine.AddRule(r);
        }

        await engine.StartAsync(config, token);

        var status = engine.GetDeploymentStatus();
        Console.WriteLine(JsonSerializer.Serialize(status, json));

        try
        {
            await Task.Delay(Timeout.Infinite, token);
        }
        catch (OperationCanceledException)
        {
            // Graceful cancellation requested
        }

        await engine.StopAsync();
        return 0;
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 2;
    }
    catch (OperationCanceledException)
    {
        return 4;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Mock server error: {ex.Message}");
        return 3;
    }
});
mock.Subcommands.Add(mockServe);

root.Subcommands.Add(mock);

var playwright = new Command("playwright", "Playwright application testing integration: evaluate captured network traffic against collection definitions and generate CI reports.");

var pwEval = new Command("evaluate", "Evaluate a captured Playwright traffic JSON file against collection definitions.");
var pwCapture = new Option<FileInfo>("--capture") { Required = true, Description = "Path to captured traffic JSON file" };
var pwRequests = new Option<FileInfo?>("--requests") { Description = "Path to collection HttpRequestDefinition JSON array" };
var pwScenario = new Option<string>("--scenario") { DefaultValueFactory = _ => "DefaultScenario", Description = "Scenario name" };
var pwUiPassed = new Option<bool>("--ui-passed") { DefaultValueFactory = _ => true, Description = "Whether the UI browser test passed" };
var pwOutputDir = new Option<DirectoryInfo?>("--output-dir") { Description = "Directory to emit CI reports (playwright-api-report.json, playwright-api-summary.md)" };

pwEval.Options.Add(pwCapture);
pwEval.Options.Add(pwRequests);
pwEval.Options.Add(pwScenario);
pwEval.Options.Add(pwUiPassed);
pwEval.Options.Add(pwOutputDir);

pwEval.SetAction(async (parse, token) =>
{
    try
    {
        var capFile = parse.GetValue(pwCapture)!;
        if (!capFile.Exists)
        {
            Console.Error.WriteLine($"Capture file not found: {capFile.FullName}");
            return 2;
        }

        var trafficText = await File.ReadAllTextAsync(capFile.FullName, token);
        var traffic = JsonSerializer.Deserialize<List<CapturedTrafficRecord>>(trafficText, json) ?? new();

        var knownRequests = new List<HttpRequestDefinition>();
        var reqFile = parse.GetValue(pwRequests);
        if (reqFile is not null && reqFile.Exists)
        {
            var reqText = await File.ReadAllTextAsync(reqFile.FullName, token);
            var parsed = JsonSerializer.Deserialize<List<HttpRequestDefinition>>(reqText, json);
            if (parsed is not null) knownRequests.AddRange(parsed);
        }

        var svc = new PlaywrightTrafficService();
        var result = svc.EvaluateScenario(
            scenarioName: parse.GetValue(pwScenario)!,
            uiPassed: parse.GetValue(pwUiPassed),
            traffic: traffic,
            knownRequests: knownRequests
        );

        var outDir = parse.GetValue(pwOutputDir);
        if (outDir is not null)
        {
            var summary = new PlaywrightSummaryReport(
                TotalScenarios: 1,
                PassedScenarios: result.CombinedPassed ? 1 : 0,
                FailedScenarios: result.CombinedPassed ? 0 : 1,
                FailingScenarios: result.CombinedPassed ? Array.Empty<string>() : new[] { result.ScenarioName },
                TotalCapturedRequests: traffic.Count,
                MatchedRequests: traffic.Count - result.UnmatchedTraffic.Count,
                UnmatchedRequests: result.UnmatchedTraffic.Count,
                AmbiguousRequests: result.AmbiguousTraffic.Count,
                TotalAssertionFailures: result.ValidationResults.Count(v => !v.AssertionsPassed),
                Scenarios: new[] { result }
            );

            svc.GenerateCiArtifacts(summary, outDir.FullName);
        }

        Console.WriteLine(JsonSerializer.Serialize(result, json));
        return result.CombinedPassed ? 0 : 1;
    }
    catch (OperationCanceledException) { return 4; }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Evaluation error: {ex.Message}");
        return 3;
    }
});

playwright.Subcommands.Add(pwEval);

root.Subcommands.Add(playwright);

var flow = new Command("flow", "Visual workflows: validate, debug, and execute text-backed workflow graphs.");

var flowRun = new Command("run", "Execute a visual workflow JSON definition.");
var flowFile = new Option<FileInfo>("--file") { Required = true, Description = "Path to FlowDefinition JSON file" };
var flowVars = new Option<FileInfo?>("--variables") { Description = "Path to initial variables JSON file" };

flowRun.Options.Add(flowFile);
flowRun.Options.Add(flowVars);

flowRun.SetAction(async (parse, token) =>
{
    try
    {
        var file = parse.GetValue(flowFile)!;
        if (!file.Exists)
        {
            Console.Error.WriteLine($"Flow file not found: {file.FullName}");
            return 2;
        }

        var text = await File.ReadAllTextAsync(file.FullName, token);
        var def = JsonSerializer.Deserialize<FlowDefinition>(text, json);
        if (def is null)
        {
            Console.Error.WriteLine("Failed to parse flow definition.");
            return 2;
        }

        var vars = new Dictionary<string, string>();
        var varFile = parse.GetValue(flowVars);
        if (varFile is not null && varFile.Exists)
        {
            var vt = await File.ReadAllTextAsync(varFile.FullName, token);
            var pv = JsonSerializer.Deserialize<Dictionary<string, string>>(vt, json);
            if (pv is not null) foreach (var (k, v) in pv) vars[k] = v;
        }

        var engine = new VisualFlowEngine();
        var report = await engine.ExecuteFlowAsync(def, vars, cancellationToken: token);
        Console.WriteLine(JsonSerializer.Serialize(report, json));

        return report.Outcome == FlowExecutionOutcome.Passed ? 0 : 1;
    }
    catch (OperationCanceledException) { return 4; }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Flow execution error: {ex.Message}");
        return 3;
    }
});

flow.Subcommands.Add(flowRun);

root.Subcommands.Add(flow);

return await root.Parse(args).InvokeAsync();
