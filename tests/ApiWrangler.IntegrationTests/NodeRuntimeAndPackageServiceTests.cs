using ApiWrangler.Application;
using ApiWrangler.Execution;
using Xunit;

namespace ApiWrangler.IntegrationTests;

public sealed class NodeRuntimeAndPackageServiceTests
{
    [Fact]
    public async Task NodeRuntimeService_DiscoversRuntime_AndValidatesLtsVersion()
    {
        var service = new NodeRuntimeService();
        NodeRuntimeInfo info = await service.DiscoverAsync();

        if (info.IsAvailable)
        {
            Assert.NotNull(info.NodePath);
            Assert.NotNull(info.Version);
            Assert.True(info.MajorVersion >= NodeRuntimeService.MinimumSupportedMajorVersion);
            Assert.True(info.IsSupportedVersion);
            Assert.Contains("Node.js", info.Diagnostics);
        }
        else
        {
            Assert.Null(info.NodePath);
            Assert.False(info.IsSupportedVersion);
            Assert.Contains("not found", info.Diagnostics, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("v20.11.0", 20)]
    [InlineData("v22.5.1", 22)]
    [InlineData("v26.13.0", 26)]
    [InlineData("18.19.0", 18)]
    [InlineData("invalid", 0)]
    [InlineData("", 0)]
    public void ParseMajorVersion_ExtractsExpectedMajor(string version, int expectedMajor)
    {
        Assert.Equal(expectedMajor, NodeRuntimeService.ParseMajorVersion(version));
    }

    [Fact]
    public void ReadPackageMetadata_WithoutPackageJson_ReturnsEmptyMetadata()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "awr-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            NpmPackageMetadata meta = NpmPackageService.ReadPackageMetadata(tempDir);
            Assert.Null(meta.Name);
            Assert.Null(meta.Version);
            Assert.Empty(meta.Dependencies);
            Assert.Empty(meta.DevDependencies);
            Assert.False(meta.HasLockfile);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ReadPackageMetadata_WithPackageJson_ExtractsDependenciesAndScriptsWithoutExecution()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "awr-pkg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string pkgJson = """
            {
              "name": "my-api-tests",
              "version": "1.0.0",
              "scripts": {
                "test": "node test.js",
                "build": "tsc"
              },
              "dependencies": {
                "lodash": "^4.17.21"
              },
              "devDependencies": {
                "@types/node": "^20.0.0"
              }
            }
            """;
            File.WriteAllText(Path.Combine(tempDir, "package.json"), pkgJson);
            File.WriteAllText(Path.Combine(tempDir, "package-lock.json"), "{}");

            NpmPackageMetadata meta = NpmPackageService.ReadPackageMetadata(tempDir);
            Assert.Equal("my-api-tests", meta.Name);
            Assert.Equal("1.0.0", meta.Version);
            Assert.True(meta.HasLockfile);
            Assert.Equal("^4.17.21", meta.Dependencies["lodash"]);
            Assert.Equal("^20.0.0", meta.DevDependencies["@types/node"]);
            Assert.Contains("test", meta.DefinedScripts);
            Assert.Contains("build", meta.DefinedScripts);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InstallPackagesAsync_UntrustedProject_ThrowsUntrustedException()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "awr-pkg-" + Guid.NewGuid().ToString("N"));
        string trustDir = Path.Combine(tempDir, "trust");
        string projectDir = Path.Combine(tempDir, "proj");
        Directory.CreateDirectory(projectDir);

        try
        {
            File.WriteAllText(Path.Combine(projectDir, "package.json"), "{}");
            File.WriteAllText(Path.Combine(projectDir, "package-lock.json"), "{}");

            var trustService = new ProjectTrustService(trustDir);
            var packageService = new NpmPackageService(trust: trustService);

            await Assert.ThrowsAsync<UntrustedScriptExecutionException>(() =>
                packageService.InstallPackagesAsync(projectDir));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task InstallPackagesAsync_MissingLockfile_ThrowsInvalidOperation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "awr-pkg-" + Guid.NewGuid().ToString("N"));
        string trustDir = Path.Combine(tempDir, "trust");
        string projectDir = Path.Combine(tempDir, "proj");
        Directory.CreateDirectory(projectDir);

        try
        {
            File.WriteAllText(Path.Combine(projectDir, "package.json"), "{}");
            // No package-lock.json

            var trustService = new ProjectTrustService(trustDir);
            await trustService.SetTrustStateAsync(projectDir, ProjectTrustState.Trusted);
            var packageService = new NpmPackageService(trust: trustService);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                packageService.InstallPackagesAsync(projectDir));
            Assert.Contains("package-lock.json", ex.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}

