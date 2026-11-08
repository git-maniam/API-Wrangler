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
            var service = new GitRepositoryService();
            await service.InitializeAsync(remote, bare: true);
            await service.CloneAsync(remote, first);
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

    [Fact]
    public async Task GitRepository_StatusReportsIndependentAheadBehindAndDirtyFacts()
    {
        var root = Path.Combine(Path.GetTempPath(), "Api Wrangler git status", Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(root, "remote.git");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second path");
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, "init", "--bare", remote);
            await Git(root, "clone", remote, first);
            Directory.CreateDirectory(Path.Combine(first, "api-wrangler"));
            await File.WriteAllTextAsync(Path.Combine(first, "api-wrangler", "fixture.txt"), "base");
            await Git(first, "add", "api-wrangler/fixture.txt");
            await Git(first, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "-c", "commit.gpgsign=false", "commit", "-m", "Base");
            await Git(first, "push", "-u", "origin", "HEAD");
            await Git(root, "clone", remote, second);
            string expectedBranch = await Git(second, "branch", "--show-current");
            await File.WriteAllTextAsync(Path.Combine(first, "api-wrangler", "fixture.txt"), "remote");
            await Git(first, "commit", "-am", "Remote update");
            await Git(first, "push");
            await Git(second, "fetch", "origin");
            await File.WriteAllTextAsync(Path.Combine(second, "api-wrangler", "local.txt"), "local");
            await Git(second, "add", "api-wrangler/local.txt");
            await Git(second, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "-c", "commit.gpgsign=false", "commit", "-m", "Local update");
            await Git(second, "fetch", "origin");
            await File.WriteAllTextAsync(Path.Combine(second, "unrelated.txt"), "dirty");

            var status = await new GitRepositoryService().GetStatusAsync(second);
            Assert.Equal(expectedBranch, status.Branch);
            Assert.True(status.IsDirty);
            Assert.Equal(1, status.Ahead);
            Assert.Equal(1, status.Behind);
            Assert.True(status.IsDiverged);
            Assert.Contains(status.Changes, change => change.Path == "unrelated.txt");
            await Assert.ThrowsAsync<IOException>(() => new GitRepositoryService().PushAsync(second, "origin"));
            Assert.Equal("local", await File.ReadAllTextAsync(Path.Combine(second, "api-wrangler", "local.txt")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new GitRepositoryService().SwitchBranchAsync(second, status.Branch!));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GitRepository_FetchFastForwardBranchAndRestoreAreExplicit()
    {
        var root = Path.Combine(Path.GetTempPath(), "Api Wrangler git workflow", Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(root, "remote.git");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        Directory.CreateDirectory(root);
        try
        {
            var service = new GitRepositoryService();
            await service.InitializeAsync(remote, bare: true);
            await service.CloneAsync(remote, first);
            Directory.CreateDirectory(Path.Combine(first, "api-wrangler"));
            await File.WriteAllTextAsync(Path.Combine(first, "api-wrangler", "asset.txt"), "one");
            await Git(first, "add", "api-wrangler/asset.txt");
            await Git(first, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "-c", "commit.gpgsign=false", "commit", "-m", "First");
            await Git(first, "push", "-u", "origin", "HEAD");
            await Git(root, "clone", remote, second);
            string defaultBranch = await Git(second, "branch", "--show-current");
            await File.WriteAllTextAsync(Path.Combine(first, "api-wrangler", "asset.txt"), "two");
            await Git(first, "commit", "-am", "Second");
            await Git(first, "push");

            await service.FetchAsync(second, "origin");
            Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(second, "api-wrangler", "asset.txt")));
            var behind = await service.GetStatusAsync(second);
            Assert.Equal(1, behind.Behind);
            await service.FastForwardAsync(second);
            Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(second, "api-wrangler", "asset.txt")));
            Assert.Equal(2, (await service.GetHistoryAsync(second)).Count);

            await service.CreateBranchAsync(second, "feature/git-workflows");
            Assert.Equal("feature/git-workflows", (await service.GetStatusAsync(second)).Branch);
            await service.SelectRemoteAsync(second, "origin", defaultBranch);
            Assert.Equal($"origin/{defaultBranch}", (await service.GetStatusAsync(second)).Upstream);
            await service.SwitchBranchAsync(second, defaultBranch);
            Assert.Equal(defaultBranch, (await service.GetStatusAsync(second)).Branch);
            await service.RestoreAssetAsync(second, "HEAD~1", "api-wrangler/asset.txt");
            Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(second, "api-wrangler", "asset.txt")));
            await Assert.ThrowsAsync<ArgumentException>(() => service.RestoreAssetAsync(second, "HEAD", "unrelated.txt"));
            await File.WriteAllTextAsync(Path.Combine(second, "api-wrangler", "asset.txt"), "two");
            await Git(second, "switch", "--detach", "HEAD");
            var detached = await service.GetStatusAsync(second);
            Assert.True(detached.IsDetached);
            Assert.Null(detached.Branch);
            string linked = Path.Combine(root, "linked worktree");
            await Git(second, "worktree", "add", linked, "feature/git-workflows");
            Assert.True((await service.GetStatusAsync(linked)).IsWorktree);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task GitRepository_OfflineFetchAndActiveMergeRebasePreserveLocalState()
    {
        var root = Path.Combine(Path.GetTempPath(), "Api Wrangler git recovery", Guid.NewGuid().ToString("N"));
        var remote = Path.Combine(root, "remote.git");
        var repo = Path.Combine(root, "working repo");
        var file = "api-wrangler/change.txt";
        var fullFile = Path.Combine(repo, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(root);
        try
        {
            var service = new GitRepositoryService();
            await service.InitializeAsync(remote, bare: true);
            await service.CloneAsync(remote, repo);
            Directory.CreateDirectory(Path.GetDirectoryName(fullFile)!);
            await File.WriteAllTextAsync(fullFile, "base");
            await Git(repo, "add", file);
            await CommitFixture(repo, "Base");
            await Git(repo, "push", "-u", "origin", "HEAD");
            string mainBranch = await Git(repo, "branch", "--show-current");

            string initialHead = await Git(repo, "rev-parse", "HEAD");
            await Git(repo, "remote", "set-url", "origin", Path.Combine(root, "unavailable-remote.git"));
            await Assert.ThrowsAsync<IOException>(() => service.FetchAsync(repo, "origin"));
            Assert.Equal(initialHead, await Git(repo, "rev-parse", "HEAD"));
            Assert.Equal("base", await File.ReadAllTextAsync(fullFile));
            await Git(repo, "remote", "set-url", "origin", remote);

            await Git(repo, "switch", "--create", "merge-topic");
            await File.WriteAllTextAsync(fullFile, "topic merge");
            await Git(repo, "add", file);
            await CommitFixture(repo, "Topic merge edit");
            await Git(repo, "switch", mainBranch);
            await File.WriteAllTextAsync(fullFile, "main merge");
            await Git(repo, "add", file);
            await CommitFixture(repo, "Main merge edit");
            await Assert.ThrowsAsync<IOException>(() => service.MergeAsync(repo, "merge-topic"));
            Assert.True((await service.GetStatusAsync(repo)).IsMergeInProgress);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.RebaseAsync(repo, "HEAD"));
            await Git(repo, "merge", "--abort");
            Assert.False((await service.GetStatusAsync(repo)).HasOperationInProgress);

            await Git(repo, "switch", "--create", "rebase-topic");
            await File.WriteAllTextAsync(fullFile, "topic rebase");
            await Git(repo, "add", file);
            await CommitFixture(repo, "Topic rebase edit");
            await Git(repo, "switch", mainBranch);
            await File.WriteAllTextAsync(fullFile, "main rebase");
            await Git(repo, "add", file);
            await CommitFixture(repo, "Main rebase edit");
            await Git(repo, "switch", "rebase-topic");
            string topicHead = await Git(repo, "rev-parse", "HEAD");
            await Assert.ThrowsAsync<IOException>(() => service.RebaseAsync(repo, mainBranch));
            Assert.True((await service.GetStatusAsync(repo)).IsRebaseInProgress);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.FastForwardAsync(repo));
            await Git(repo, "rebase", "--abort");
            Assert.Equal(topicHead, await Git(repo, "rev-parse", "HEAD"));
            Assert.Equal("topic rebase", await File.ReadAllTextAsync(fullFile));
        }
        finally
        {
            foreach (var entry in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(entry, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static Task<string> CommitFixture(string repository, string message) =>
        Git(repository, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.test", "-c", "commit.gpgsign=false", "commit", "-m", message);

    private static async Task<string> Git(string workingDirectory, params string[] args)
    {
        var result = await ProcessRunner.RunAsync("git", args, workingDirectory);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }
}
