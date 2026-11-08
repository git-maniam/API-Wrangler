using ApiWrangler.Application;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class ProjectTrustServiceTests
{
    [Fact]
    public async Task GetTrustStatus_NewProject_IsUntrustedByDefault()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "awr-trust-" + Guid.NewGuid().ToString("N"));
        string projectDir = Path.Combine(tempDir, "sample-project");
        Directory.CreateDirectory(projectDir);

        try
        {
            var service = new ProjectTrustService(tempDir);
            ProjectTrustStatus status = await service.GetTrustStatusAsync(projectDir);

            Assert.False(status.IsTrusted);
            Assert.False(status.HasScriptChanges);
            Assert.Contains("not trusted", status.Description, StringComparison.OrdinalIgnoreCase);

            await Assert.ThrowsAsync<UntrustedScriptExecutionException>(() => service.RequireTrustAsync(projectDir));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task SetTrustState_PersistsTrust_AndValidatesHash()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "awr-trust-" + Guid.NewGuid().ToString("N"));
        string projectDir = Path.Combine(tempDir, "trusted-project");
        Directory.CreateDirectory(projectDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(projectDir, "script.js"), "console.log('hello');");
            var service = new ProjectTrustService(tempDir);

            await service.SetTrustStateAsync(projectDir, ProjectTrustState.Trusted);

            ProjectTrustStatus status = await service.GetTrustStatusAsync(projectDir);
            Assert.True(status.IsTrusted);
            Assert.False(status.HasScriptChanges);
            Assert.NotNull(status.RecordedScriptHash);

            // Re-instantiate service pointing to same storage directory to verify persistence
            var secondService = new ProjectTrustService(tempDir);
            ProjectTrustStatus reloaded = await secondService.GetTrustStatusAsync(projectDir);
            Assert.True(reloaded.IsTrusted);
            Assert.Equal(status.CurrentScriptHash, reloaded.CurrentScriptHash);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ScriptModification_FlagsScriptChanges_AndRevokesExecution()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "awr-trust-" + Guid.NewGuid().ToString("N"));
        string projectDir = Path.Combine(tempDir, "modified-project");
        Directory.CreateDirectory(projectDir);

        try
        {
            string scriptFile = Path.Combine(projectDir, "pre-request.js");
            await File.WriteAllTextAsync(scriptFile, "variables.set('init', 1);");

            var service = new ProjectTrustService(tempDir);
            await service.SetTrustStateAsync(projectDir, ProjectTrustState.Trusted);

            Assert.True((await service.GetTrustStatusAsync(projectDir)).IsTrusted);

            // Modify the script content
            await File.WriteAllTextAsync(scriptFile, "variables.set('tampered', 2);");

            ProjectTrustStatus status = await service.GetTrustStatusAsync(projectDir);
            Assert.False(status.IsTrusted);
            Assert.True(status.HasScriptChanges);
            Assert.Contains("modified", status.Description, StringComparison.OrdinalIgnoreCase);

            await Assert.ThrowsAsync<UntrustedScriptExecutionException>(() => service.RequireTrustAsync(projectDir));

            // Re-trusting records the new hash
            await service.SetTrustStateAsync(projectDir, ProjectTrustState.Trusted);
            ProjectTrustStatus updated = await service.GetTrustStatusAsync(projectDir);
            Assert.True(updated.IsTrusted);
            Assert.False(updated.HasScriptChanges);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ComputeScriptHash_ExcludesNodeModulesAndGitDirectories()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "awr-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "test.js"), "const x = 1;");
            string hash1 = ProjectTrustService.ComputeScriptHash(tempDir);

            // Add node_modules and .git files - these should NOT alter script hash
            Directory.CreateDirectory(Path.Combine(tempDir, "node_modules", "pkg"));
            await File.WriteAllTextAsync(Path.Combine(tempDir, "node_modules", "pkg", "index.js"), "module.exports = {};");
            Directory.CreateDirectory(Path.Combine(tempDir, ".git", "hooks"));
            await File.WriteAllTextAsync(Path.Combine(tempDir, ".git", "hooks", "pre-commit.js"), "#!/usr/bin/env node");

            string hash2 = ProjectTrustService.ComputeScriptHash(tempDir);
            Assert.Equal(hash1, hash2);

            // Modifying test.js MUST change hash
            await File.WriteAllTextAsync(Path.Combine(tempDir, "test.js"), "const x = 2;");
            string hash3 = ProjectTrustService.ComputeScriptHash(tempDir);
            Assert.NotEqual(hash1, hash3);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
