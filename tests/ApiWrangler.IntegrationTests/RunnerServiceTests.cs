using ApiWrangler.Application;
using ApiWrangler.Domain;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class RunnerServiceTests
{
    [Fact]
    public void RunnerEnrollment_GeneratesValidToken_AndEnforcesScopes()
    {
        var service = new RunnerEnrollmentService();

        // 1. Create RunnerRegistration token
        var regRes = service.CreateToken("Worker-1", RunnerEnrollmentScope.RunnerRegistration, TimeSpan.FromDays(30));
        Assert.True(regRes.Success);
        Assert.NotNull(regRes.PlaintextToken);
        Assert.StartsWith("awr_tok_", regRes.PlaintextToken);

        // 2. Validate for RunnerRegistration -> success
        var validReg = service.ValidateToken(regRes.PlaintextToken, RunnerEnrollmentScope.RunnerRegistration, out var token1);
        Assert.True(validReg);
        Assert.NotNull(token1);
        Assert.Equal(RunnerEnrollmentScope.RunnerRegistration, token1.Scope);

        // 3. Validate for JobSubmission -> scope mismatch
        var invalidScope = service.ValidateToken(regRes.PlaintextToken, RunnerEnrollmentScope.JobSubmission, out _);
        Assert.False(invalidScope);

        // 4. Admin token inherits all scopes
        var adminRes = service.CreateToken("Admin-Token", RunnerEnrollmentScope.Admin);
        Assert.True(adminRes.Success);
        Assert.True(service.ValidateToken(adminRes.PlaintextToken!, RunnerEnrollmentScope.RunnerRegistration, out _));
        Assert.True(service.ValidateToken(adminRes.PlaintextToken!, RunnerEnrollmentScope.JobSubmission, out _));
        Assert.True(service.ValidateToken(adminRes.PlaintextToken!, RunnerEnrollmentScope.Admin, out _));
    }

    [Fact]
    public void RunnerEnrollment_EnforcesExpiration_AndRevocation()
    {
        var service = new RunnerEnrollmentService();

        // 1. Expired token
        var expiredRes = service.CreateToken("Short-Lived", RunnerEnrollmentScope.RunnerRegistration, TimeSpan.FromSeconds(-10));
        Assert.True(expiredRes.Success);
        var validExpired = service.ValidateToken(expiredRes.PlaintextToken!, RunnerEnrollmentScope.RunnerRegistration, out _);
        Assert.False(validExpired);

        // 2. Revocation
        var revRes = service.CreateToken("Revocable", RunnerEnrollmentScope.RunnerRegistration);
        Assert.True(service.ValidateToken(revRes.PlaintextToken!, RunnerEnrollmentScope.RunnerRegistration, out _));
        service.RevokeToken(revRes.TokenId!);
        Assert.False(service.ValidateToken(revRes.PlaintextToken!, RunnerEnrollmentScope.RunnerRegistration, out _));

        // 3. Rotation
        var rotRes = service.CreateToken("To-Rotate", RunnerEnrollmentScope.JobSubmission);
        Assert.True(service.ValidateToken(rotRes.PlaintextToken!, RunnerEnrollmentScope.JobSubmission, out _));

        var rotated = service.RotateToken(rotRes.TokenId!, TimeSpan.FromDays(7));
        Assert.True(rotated.Success);
        Assert.NotNull(rotated.PlaintextToken);

        // Old token is revoked
        Assert.False(service.ValidateToken(rotRes.PlaintextToken!, RunnerEnrollmentScope.JobSubmission, out _));
        // New token is valid
        Assert.True(service.ValidateToken(rotated.PlaintextToken, RunnerEnrollmentScope.JobSubmission, out _));
    }

    [Fact]
    public async Task ExactCommitJobExecutor_EnforcesExactCommitSha_AndWritesManifest()
    {
        var executor = new ExactCommitJobExecutor(runnerId: "test-runner-1", runnerVersion: "1.0.0-test");
        var tempDir = Path.Combine(Path.GetTempPath(), "awr-runner-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            // Invalid commit SHA (< 7 chars)
            var badJob = new RunnerJobDefinition(
                JobId: "job-bad",
                ProjectId: "proj-1",
                RepositoryUrl: "https://github.com/org/repo.git",
                ExactCommitSha: "abc"
            );
            var badResult = await executor.ExecuteJobAsync(badJob, tempDir);
            Assert.Equal(RunnerJobOutcome.Failed, badResult.Outcome);
            Assert.Contains("ExactCommitSha must be specified", badResult.Summary);

            // Valid exact commit SHA
            const string commitSha = "d0dc6e18f2922718cfba488d5761a2be0055cb21";
            var goodJob = new RunnerJobDefinition(
                JobId: "job-good",
                ProjectId: "proj-1",
                RepositoryUrl: "https://github.com/org/repo.git",
                ExactCommitSha: commitSha
            );

            bool delegateInvoked = false;
            var goodResult = await executor.ExecuteJobAsync(goodJob, tempDir, executionDelegate: (dir, secrets, ct) =>
            {
                delegateInvoked = true;
                Assert.True(Directory.Exists(dir));
                Assert.True(File.Exists(Path.Combine(dir, "execution-manifest.json")));
                return Task.FromResult(0);
            });

            Assert.True(delegateInvoked);
            Assert.Equal(RunnerJobOutcome.Passed, goodResult.Outcome);
            Assert.Equal(0, goodResult.ExitCode);
            Assert.Equal(commitSha, goodResult.ExecutedCommitSha);
            Assert.Single(goodResult.ArtifactPaths);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExactCommitJobExecutor_CrashesYieldIndeterminateOutcome_PreventingSilentRetries()
    {
        var executor = new ExactCommitJobExecutor();
        var tempDir = Path.Combine(Path.GetTempPath(), "awr-crash-test-" + Guid.NewGuid().ToString("N"));

        try
        {
            var mutatingJob = new RunnerJobDefinition(
                JobId: "job-mutating",
                ProjectId: "proj-mutation",
                RepositoryUrl: "https://github.com/org/repo.git",
                ExactCommitSha: "1234567890abcdef1234567890abcdef12345678"
            );

            var result = await executor.ExecuteJobAsync(mutatingJob, tempDir, executionDelegate: (_, _, _) =>
            {
                throw new InvalidOperationException("Simulated worker process crash or connection loss mid-execution");
            });

            // Must NOT be marked as normal failure or silently retried!
            Assert.Equal(RunnerJobOutcome.Indeterminate, result.Outcome);
            Assert.NotNull(result.IndeterminateReason);
            Assert.Contains("Process terminated unexpectedly", result.IndeterminateReason);
            Assert.Contains("Manual review required", result.IndeterminateReason);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void ExactCommitJobExecutor_MatchesCapabilityLabels_AndResolvesHostSecrets()
    {
        var runnerCaps = new[] { "windows", "x64", "dotnet", "node" };

        // Matching subset
        Assert.True(ExactCommitJobExecutor.MatchCapabilities(runnerCaps, new[] { "dotnet" }));
        Assert.True(ExactCommitJobExecutor.MatchCapabilities(runnerCaps, new[] { "windows", "x64" }));
        Assert.True(ExactCommitJobExecutor.MatchCapabilities(runnerCaps, null));
        Assert.True(ExactCommitJobExecutor.MatchCapabilities(runnerCaps, Array.Empty<string>()));

        // Missing required capability
        Assert.False(ExactCommitJobExecutor.MatchCapabilities(runnerCaps, new[] { "linux" }));
        Assert.False(ExactCommitJobExecutor.MatchCapabilities(runnerCaps, new[] { "docker" }));

        // Host secrets isolation
        Environment.SetEnvironmentVariable("API_WRANGLER_SECRET_DATABASE_KEY", "secret-db-value-123");
        try
        {
            var secrets = ExactCommitJobExecutor.ResolveHostSecrets();
            Assert.True(secrets.ContainsKey("DATABASE_KEY"));
            Assert.Equal("secret-db-value-123", secrets["DATABASE_KEY"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("API_WRANGLER_SECRET_DATABASE_KEY", null);
        }
    }
}

