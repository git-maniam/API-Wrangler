using ApiWrangler.Application;
using ApiWrangler.Domain;
using ApiWrangler.Execution;

namespace ApiWrangler.IntegrationTests;

public sealed class FoundationTests
{
    [Fact]
    public void Dataset_RandomAccess_KeepsOnlyFourPages()
    {
        var data = new PagedDataset();
        Assert.Equal(100_000, data.Count);
        Assert.Equal(0, data.CachedRowCount);
        for (var i = 0; i < data.Count; i += 400)
            Assert.Equal($"case-{i:D6}", ((DatasetRow)data[i]!).CaseId);
        Assert.InRange(data.CachedRowCount, 1, 1024);
        Assert.Equal("case-099999", ((DatasetRow)data[99_999]!).CaseId);
    }

    [Fact]
    public void DomainAndEngine_DoNotReferenceDesktopFrameworks()
    {
        var domain = typeof(HttpRequestDefinition).Assembly.GetReferencedAssemblies();
        Assert.DoesNotContain(domain, a => a.Name!.StartsWith("ApiWrangler.") || a.Name.Contains("Sqlite"));
        foreach (var assembly in new[] { typeof(HttpRequestDefinition).Assembly, typeof(HttpRequestService).Assembly, typeof(HttpTransport).Assembly })
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), a => a.Name is "PresentationFramework" or "WindowsBase" || a.Name!.Contains("Desktop"));
    }

    [Fact]
    public async Task Git_Fetch_UpdatesRemoteRefsWithoutChangingWorkingFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "Api Wrangler git tests", Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(root, "remote.git");
        var first = Path.Combine(root, "first café");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, "init", "--bare", remote);
            await Git(root, "clone", remote, first);
            await File.WriteAllTextAsync(Path.Combine(first, "asset.txt"), "original");
            await Git(first, "add", "asset.txt");
            await Git(first, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "-c", "commit.gpgsign=false", "commit", "-m", "Initial fixture");
            await Git(first, "push", "origin", "HEAD");
            await Git(root, "clone", remote, second);
            var before = await Git(second, "rev-parse", "HEAD");
            await File.WriteAllTextAsync(Path.Combine(first, "asset.txt"), "incoming");
            await Git(first, "add", "asset.txt");
            await Git(first, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "-c", "commit.gpgsign=false", "commit", "-m", "Update fixture");
            await Git(first, "push", "origin", "HEAD");
            var result = await new GitProbe().FetchAsync(second);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(before, await Git(second, "rev-parse", "HEAD"));
            Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(second, "asset.txt")));
            Assert.NotEqual(before, await Git(second, "rev-parse", "FETCH_HEAD"));
        }
        finally
        {
            // Only the unique temporary fixture tree is removed; clear Git's read-only pack attributes first.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static async Task<string> Git(string workingDirectory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }
}
