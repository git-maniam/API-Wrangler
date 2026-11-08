using ApiWrangler.Execution;

namespace ApiWrangler.IntegrationTests;

public sealed class GitCommitReviewServiceTests
{
    [Fact]
    public async Task Review_StagesOnlySelectedAssetsAndReturnsExactDiffAndTree()
    {
        string root = NewRepositoryPath();
        try
        {
            await InitializeAsync(root);
            Directory.CreateDirectory(Path.Combine(root, "api-wrangler"));
            await File.WriteAllTextAsync(Path.Combine(root, "api-wrangler", "project.json"), "{\"name\":\"fixture\"}");
            await File.WriteAllTextAsync(Path.Combine(root, "outside.txt"), "do not stage");
            GitCommitReview review = await new GitCommitReviewService().ReviewAsync(root, ["api-wrangler/project.json"]);
            Assert.Contains("project.json", review.CachedDiff);
            Assert.Matches("^[0-9a-f]{40,64}$", review.IndexTreeHash);
            Assert.True(review.CanCommit);
            Assert.Equal("api-wrangler/project.json", await GitAsync(root, "diff", "--cached", "--name-only"));
            Assert.Contains("not performed", review.SchemaValidationNote);
        }
        finally { DeleteTree(root); }
    }

    [Fact]
    public async Task Review_RefusesUnrelatedStagedPathBeforeChangingIndex()
    {
        string root = NewRepositoryPath();
        try
        {
            await InitializeAsync(root);
            Directory.CreateDirectory(Path.Combine(root, "api-wrangler"));
            await File.WriteAllTextAsync(Path.Combine(root, "api-wrangler", "asset.txt"), "asset");
            await File.WriteAllTextAsync(Path.Combine(root, "outside.txt"), "outside");
            await GitAsync(root, "add", "--", "outside.txt");
            string before = await GitAsync(root, "write-tree");
            await Assert.ThrowsAsync<InvalidOperationException>(() => new GitCommitReviewService().ReviewAsync(root, ["api-wrangler/asset.txt"]));
            Assert.Equal(before, await GitAsync(root, "write-tree"));
        }
        finally { DeleteTree(root); }
    }

    [Fact]
    public async Task Review_FlagsCredentialsAndInvalidJsonAndCommitRequiresUnchangedConfirmedTree()
    {
        string root = NewRepositoryPath();
        try
        {
            await InitializeAsync(root);
            Directory.CreateDirectory(Path.Combine(root, "api-wrangler"));
            await File.WriteAllTextAsync(Path.Combine(root, "api-wrangler", "project.json"), "{ invalid AKIAABCDEFGHIJKLMNOP }");
            var service = new GitCommitReviewService();
            GitCommitReview review = await service.ReviewAsync(root, ["api-wrangler/project.json"]);
            Assert.False(review.CanCommit);
            Assert.NotEmpty(review.SecretFindings);
            Assert.NotEmpty(review.JsonSyntaxErrors);
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CommitAsync(review, "blocked", review.IndexTreeHash));
        }
        finally { DeleteTree(root); }
    }

    [Fact]
    public async Task Commit_RequiresCallerConfirmationOfReviewedIndexTree()
    {
        string root = NewRepositoryPath();
        try
        {
            await InitializeAsync(root);
            Directory.CreateDirectory(Path.Combine(root, "api-wrangler"));
            await File.WriteAllTextAsync(Path.Combine(root, "api-wrangler", "asset.txt"), "reviewed");
            var service = new GitCommitReviewService();
            GitCommitReview review = await service.ReviewAsync(root, ["api-wrangler/asset.txt"]);
            await File.WriteAllTextAsync(Path.Combine(root, "api-wrangler", "other.txt"), "later change");
            await GitAsync(root, "add", "--", "api-wrangler/other.txt");
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CommitAsync(review, "reviewed assets", review.IndexTreeHash));
            ProcessResult head = await ProcessRunner.RunAsync("git", ["rev-parse", "--verify", "HEAD"], root);
            Assert.NotEqual(0, head.ExitCode);
        }
        finally { DeleteTree(root); }
    }

    [Fact]
    public async Task Commit_RefusesBranchChangeAfterReviewEvenWhenIndexTreeIsUnchanged()
    {
        string root = NewRepositoryPath();
        try
        {
            await InitializeAsync(root);
            Directory.CreateDirectory(Path.Combine(root, "api-wrangler"));
            await File.WriteAllTextAsync(Path.Combine(root, "api-wrangler", "asset.txt"), "reviewed");
            var service = new GitCommitReviewService();
            GitCommitReview review = await service.ReviewAsync(root, ["api-wrangler/asset.txt"]);
            await GitAsync(root, "switch", "--create", "review-race");
            Assert.Equal(review.IndexTreeHash, await GitAsync(root, "write-tree"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CommitAsync(review, "stale branch", review.IndexTreeHash));
            Assert.NotEqual(0, (await ProcessRunner.RunAsync("git", ["rev-parse", "--verify", "HEAD"], root)).ExitCode);
        }
        finally { DeleteTree(root); }
    }

    private static string NewRepositoryPath() => Path.Combine(Path.GetTempPath(), "Api Wrangler commit review", Guid.NewGuid().ToString("N"));

    private static async Task InitializeAsync(string root)
    {
        Directory.CreateDirectory(root);
        await GitAsync(root, "init");
        await GitAsync(root, "config", "user.name", "Fixture");
        await GitAsync(root, "config", "user.email", "fixture@example.test");
        await GitAsync(root, "config", "commit.gpgsign", "false");
    }

    private static async Task<string> GitAsync(string root, params string[] args)
    {
        ProcessResult result = await ProcessRunner.RunAsync("git", args, root);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }
}
