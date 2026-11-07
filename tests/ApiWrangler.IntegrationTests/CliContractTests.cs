using System.Text.Json.Nodes;
using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Execution;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class CliContractTests : IDisposable
{
    private readonly string testDirectory = Path.Combine(Path.GetTempPath(), "ApiWrangler-cli-contract-" + Guid.NewGuid().ToString("N"));

    public CliContractTests()
    {
        Directory.CreateDirectory(testDirectory);
    }

    private static string FindCliDll()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "api-wrangler.dll");
        if (File.Exists(local)) return local;

        var candidates = new[]
        {
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/ApiWrangler.Cli/bin/Release/net10.0/api-wrangler.dll")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/ApiWrangler.Cli/bin/Debug/net10.0/api-wrangler.dll")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../src/ApiWrangler.Cli/bin/Release/net10.0/api-wrangler.dll")),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../src/ApiWrangler.Cli/bin/Debug/net10.0/api-wrangler.dll"))
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var cliRel = Path.Combine(dir.FullName, "src", "ApiWrangler.Cli", "bin", "Release", "net10.0", "api-wrangler.dll");
            if (File.Exists(cliRel)) return cliRel;
            var cliDbg = Path.Combine(dir.FullName, "src", "ApiWrangler.Cli", "bin", "Debug", "net10.0", "api-wrangler.dll");
            if (File.Exists(cliDbg)) return cliDbg;
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate api-wrangler.dll.");
    }

    [Fact]
    public async Task Cli_InitAndValidate_ExecutesSuccessfully_WithoutDesktop()
    {
        var cliDll = FindCliDll();
        var projDir = Path.Combine(testDirectory, "my-api-project");

        // 1. Run "init"
        var initRes = await ProcessRunner.RunAsync("dotnet", [cliDll, "init", "--project", projDir, "--name", "Checkout Suite"], testDirectory);
        Assert.Equal(0, initRes.ExitCode);
        Assert.Contains("initialized", initRes.StandardOutput);

        Assert.True(File.Exists(Path.Combine(projDir, "wrangler.json")));
        Assert.True(File.Exists(Path.Combine(projDir, "collections", "default", "collection.json")));
        Assert.True(File.Exists(Path.Combine(projDir, "collections", "default", "requests", "hello.request.json")));
        Assert.True(File.Exists(Path.Combine(projDir, "environments", "local.environment.json")));

        // 2. Run "init" again on same directory -> should return exit code 2 (Invalid config/already exists)
        var duplicateInit = await ProcessRunner.RunAsync("dotnet", [cliDll, "init", "--project", projDir], testDirectory);
        Assert.Equal(2, duplicateInit.ExitCode);

        // 3. Run "validate" on valid project -> should return exit code 0
        var valRes = await ProcessRunner.RunAsync("dotnet", [cliDll, "validate", "--project", projDir], testDirectory);
        Assert.Equal(0, valRes.ExitCode);
        Assert.Contains("\"valid\": true", valRes.StandardOutput);

        // 4. Run "validate" on non-existent manifest directory -> should return exit code 2
        var invalidVal = await ProcessRunner.RunAsync("dotnet", [cliDll, "validate", "--project", testDirectory], testDirectory);
        Assert.Equal(2, invalidVal.ExitCode);
    }

    [Fact]
    public async Task Cli_Run_WithReporters_GeneratesArtifactsAndPreservesEvidence()
    {
        var cliDll = FindCliDll();
        var projDir = Path.Combine(testDirectory, "run-proj");
        var reportDir = Path.Combine(testDirectory, "ci-reports");
        var runDir = Path.Combine(testDirectory, "ci-runs");

        // Initialize sample project
        var initRes = await ProcessRunner.RunAsync("dotnet", [cliDll, "init", "--project", projDir], testDirectory);
        Assert.Equal(0, initRes.ExitCode);

        // Run the "Getting Started" collection
        var runRes = await ProcessRunner.RunAsync("dotnet",
        [
            cliDll,
            "run",
            "--project", projDir,
            "--collection", "Default collection",
            "--reporters", "console,junit,json,html,markdown",
            "--report-dir", reportDir,
            "--output", runDir
        ], testDirectory);

        // Echo server is not running on localhost:5057 so transport error or assertion fail occurs,
        // producing exit code 1 or 3 (non-zero), BUT all reports and failure artifacts must be preserved!
        Assert.True(Directory.Exists(reportDir));
        Assert.True(File.Exists(Path.Combine(reportDir, "junit.xml")));
        Assert.True(File.Exists(Path.Combine(reportDir, "report.json")));
        Assert.True(File.Exists(Path.Combine(reportDir, "report.html")));
        Assert.True(File.Exists(Path.Combine(reportDir, "report.md")));

        var junitContent = await File.ReadAllTextAsync(Path.Combine(reportDir, "junit.xml"));
        Assert.Contains("<testsuites", junitContent);
    }

    [Fact]
    public async Task HostEnvironmentSecrets_AreResolvedInNoninteractiveMode()
    {
        const string secretKey = "HOST_CI_TOKEN";
        const string secretValue = "ci-secret-token-xyz987";
        Environment.SetEnvironmentVariable($"API_WRANGLER_SECRET_{secretKey}", secretValue);

        try
        {
            var context = new VariableContext();
            var resolver = new VariableResolver();

            var resolved = await resolver.ResolveAsync($"{{{{secret:{secretKey}}}}}", context, "test");
            Assert.Equal(secretValue, resolved);

            var inspection = await resolver.InspectAsync($"{{{{secret:{secretKey}}}}}", context);
            Assert.Single(inspection);
            Assert.Equal("[REDACTED]", inspection[0].RedactedValue);
            Assert.True(inspection[0].IsSecret);
        }
        finally
        {
            Environment.SetEnvironmentVariable($"API_WRANGLER_SECRET_{secretKey}", null);
        }
    }

    [Fact]
    public async Task Cli_ExitCode_PrecedenceContract()
    {
        var cliDll = FindCliDll();

        // 1. Missing project argument or invalid collection -> Exit code 2
        var badArgs = await ProcessRunner.RunAsync("dotnet", [cliDll, "run", "--project", "non-existent-dir-12345", "--collection", "unknown"], testDirectory);
        Assert.Equal(2, badArgs.ExitCode);

        // 2. Git probe on non-existent repo -> Exit code 2
        var gitBad = await ProcessRunner.RunAsync("dotnet", [cliDll, "git-probe", "--repository", "non-existent-dir-12345"], testDirectory);
        Assert.Equal(2, gitBad.ExitCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(testDirectory))
        {
            try { Directory.Delete(testDirectory, true); } catch { }
        }
    }
}
