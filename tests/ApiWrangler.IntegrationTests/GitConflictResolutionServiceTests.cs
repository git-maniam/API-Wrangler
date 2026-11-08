using System.Text;
using System.Text.Json.Nodes;
using ApiWrangler.Execution;

namespace ApiWrangler.IntegrationTests;

public sealed class GitConflictResolutionServiceTests
{
    [Fact]
    public async Task TwoClones_MergeIndependentJsonEditsAndPublishWithoutLosingUnrelatedWork()
    {
        using var fixture = await ConflictFixture.CreateAsync("api-wrangler/request.json",
            "{\"name\":\"base\",\"timeout\":10,\"future\":true}",
            "{\"name\":\"local\",\"timeout\":10,\"future\":true}",
            "{\"name\":\"base\",\"timeout\":20,\"future\":true}");
        var service = new GitConflictResolutionService();
        string before = await File.ReadAllTextAsync(fixture.FullPath);
        string head = await GitAsync(fixture.Local, "rev-parse", "HEAD");
        string stages = await GitAsync(fixture.Local, "ls-files", "--unmerged");

        Assert.Equal(fixture.AssetPath, Assert.Single(await service.ListConflictsAsync(fixture.Local)));
        GitConflictSnapshot snapshot = await service.ReadAsync(fixture.Local, fixture.AssetPath);
        Assert.Empty(snapshot.Conflicts);
        Assert.Equal("base", JsonNode.Parse(snapshot.BaseText!)!["name"]!.GetValue<string>());
        Assert.Equal("local", JsonNode.Parse(snapshot.LocalText!)!["name"]!.GetValue<string>());
        Assert.Equal(20, JsonNode.Parse(snapshot.IncomingText!)!["timeout"]!.GetValue<int>());
        GitConflictPreview preview = await service.CreatePreviewAsync(snapshot, GitConflictChoice.EditedText, snapshot.ProposalText);
        Assert.Contains("local", preview.FinalDiff, StringComparison.Ordinal);
        Assert.Contains("20", preview.FinalDiff, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllTextAsync(fixture.FullPath));
        Assert.Equal(stages, await GitAsync(fixture.Local, "ls-files", "--unmerged"));

        bool validationCalled = false;
        string recovery = await service.ResolveAsync(preview, preview.ReviewHash, (context, _) =>
        {
            validationCalled = true;
            Assert.Equal(fixture.AssetPath, context.Path);
            JsonNode result = JsonNode.Parse(context.Content!)!;
            Assert.Equal("local", result["name"]!.GetValue<string>());
            Assert.Equal(20, result["timeout"]!.GetValue<int>());
            Assert.True(result["future"]!.GetValue<bool>());
            return Task.CompletedTask;
        });

        Assert.True(validationCalled);
        Assert.Empty(await service.ListConflictsAsync(fixture.Local));
        Assert.Equal(head, await GitAsync(fixture.Local, "rev-parse", "HEAD"));
        Assert.True((await new GitRepositoryService().GetStatusAsync(fixture.Local)).IsMergeInProgress);
        Assert.True(Directory.Exists(recovery));
        Assert.False(Path.GetRelativePath(Path.Combine(fixture.Local, "api-wrangler"), recovery).StartsWith("api-wrangler", StringComparison.Ordinal));
        Assert.Contains(Directory.EnumerateFiles(recovery, "*", SearchOption.AllDirectories), file =>
            File.ReadAllBytes(file).SequenceEqual(Encoding.UTF8.GetBytes(before)));
        Assert.Equal("unstaged unrelated work", await File.ReadAllTextAsync(Path.Combine(fixture.Local, "unrelated.txt")));
        Assert.DoesNotContain("unrelated.txt", await GitAsync(fixture.Local, "diff", "--cached", "--name-only"), StringComparison.Ordinal);

        await GitAsync(fixture.Local, "commit", "-m", "Resolve team request edits");
        await GitAsync(fixture.Local, "push", "origin", "HEAD");
        await GitAsync(fixture.Incoming, "pull", "--ff-only");
        Assert.Equal(await File.ReadAllBytesAsync(fixture.FullPath),
            await File.ReadAllBytesAsync(Path.Combine(fixture.Incoming, fixture.AssetPath)));
        Assert.True(Directory.Exists(recovery));
    }

    [Fact]
    public async Task SameFieldConflict_RequiresConfirmedPreviewBeforeExplicitEditedResolution()
    {
        using var fixture = await ConflictFixture.CreateAsync("api-wrangler/request.json",
            "{\"name\":\"base\"}", "{\"name\":\"local\"}", "{\"name\":\"incoming\"}");
        var service = new GitConflictResolutionService();
        GitConflictSnapshot snapshot = await service.ReadAsync(fixture.Local, fixture.AssetPath);
        Assert.Contains(snapshot.Conflicts, conflict => conflict.Path == "/name");
        GitConflictPreview preview = await service.CreatePreviewAsync(snapshot, GitConflictChoice.EditedText, "{\"name\":\"chosen\"}\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAsync(preview, "unreviewed", AcceptValidation));
        Assert.Single(await service.ListConflictsAsync(fixture.Local));
        await service.ResolveAsync(preview, preview.ReviewHash, AcceptValidation);
        Assert.Equal("chosen", JsonNode.Parse(await File.ReadAllTextAsync(fixture.FullPath))!["name"]!.GetValue<string>());
        Assert.Empty(await GitAsync(fixture.Local, "ls-files", "--unmerged"));
    }

    [Fact]
    public async Task ValidationFailure_PreservesIndexAndWorkingCopyForRetry()
    {
        using var fixture = await ConflictFixture.CreateAsync("api-wrangler/request.json", "{\"id\":\"base\"}", "{\"id\":\"local\"}", "{\"id\":\"incoming\"}");
        var service = new GitConflictResolutionService();
        GitConflictPreview preview = await service.CreatePreviewAsync(await service.ReadAsync(fixture.Local, fixture.AssetPath), GitConflictChoice.Local);
        byte[] before = await File.ReadAllBytesAsync(fixture.FullPath);
        string stages = await GitAsync(fixture.Local, "ls-files", "--unmerged");

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ResolveAsync(preview, preview.ReviewHash,
            (_, _) => throw new InvalidDataException("Reference validation failed.")));

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.FullPath));
        Assert.Equal(stages, await GitAsync(fixture.Local, "ls-files", "--unmerged"));
        await service.ResolveAsync(preview, preview.ReviewHash, AcceptValidation);
        Assert.Empty(await service.ListConflictsAsync(fixture.Local));
    }

    [Theory]
    [InlineData("worktree")]
    [InlineData("index")]
    [InlineData("operation")]
    public async Task StalePreview_RejectsChangedWorkingCopyIndexOrMergeState(string change)
    {
        using var fixture = await ConflictFixture.CreateAsync("api-wrangler/script.js", "base();\n", "local();\n", "incoming();\n");
        var service = new GitConflictResolutionService();
        GitConflictPreview preview = await service.CreatePreviewAsync(await service.ReadAsync(fixture.Local, fixture.AssetPath), GitConflictChoice.Local);
        if (change == "operation")
            await GitAsync(fixture.Local, "merge", "--abort");
        else
        {
            await File.WriteAllTextAsync(fixture.FullPath, "external edit\n");
            if (change == "index") await GitAsync(fixture.Local, "add", "--", fixture.AssetPath);
        }
        byte[] before = await File.ReadAllBytesAsync(fixture.FullPath);
        string index = await GitAsync(fixture.Local, "ls-files", "--stage");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveAsync(preview, preview.ReviewHash, AcceptValidation));

        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.FullPath));
        Assert.Equal(index, await GitAsync(fixture.Local, "ls-files", "--stage"));
    }

    [Theory]
    [InlineData("{ invalid }")]
    [InlineData("{\"rows\":[{\"_caseId\":\"a\"},{\"_caseId\":\"a\"}]}")]
    public async Task InvalidJsonOrDuplicateIdentity_CannotBeMarkedResolved(string candidate)
    {
        using var fixture = await ConflictFixture.CreateAsync("api-wrangler/dataset.json", "{\"name\":\"base\"}", "{\"name\":\"local\"}", "{\"name\":\"incoming\"}");
        var service = new GitConflictResolutionService();
        GitConflictSnapshot snapshot = await service.ReadAsync(fixture.Local, fixture.AssetPath);
        byte[] before = await File.ReadAllBytesAsync(fixture.FullPath);
        string stages = await GitAsync(fixture.Local, "ls-files", "--unmerged");

        Exception? error = await Record.ExceptionAsync(async () =>
        {
            GitConflictPreview preview = await service.CreatePreviewAsync(snapshot, GitConflictChoice.EditedText, candidate);
            await service.ResolveAsync(preview, preview.ReviewHash, AcceptValidation);
        });

        Assert.NotNull(error);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.FullPath));
        Assert.Equal(stages, await GitAsync(fixture.Local, "ls-files", "--unmerged"));
    }

    [Fact]
    public async Task ScriptResolution_WritesChosenTextWithoutExecutingIt()
    {
        using var fixture = await ConflictFixture.CreateAsync("api-wrangler/script.ps1", "Write-Output 'base'\n", "Write-Output 'local'\n", "Write-Output 'incoming'\n");
        string marker = Path.Combine(fixture.Local, "executed.txt");
        string script = "Set-Content -LiteralPath '" + marker.Replace("'", "''", StringComparison.Ordinal) + "' -Value 'must not run'\n";
        var service = new GitConflictResolutionService();
        GitConflictSnapshot snapshot = await service.ReadAsync(fixture.Local, fixture.AssetPath);
        Assert.False(snapshot.IsBinary);
        GitConflictPreview preview = await service.CreatePreviewAsync(snapshot, GitConflictChoice.EditedText, script);
        Assert.Contains("must not run", preview.FinalDiff, StringComparison.Ordinal);

        await service.ResolveAsync(preview, preview.ReviewHash, AcceptValidation);

        Assert.Equal(script, await File.ReadAllTextAsync(fixture.FullPath));
        Assert.False(File.Exists(marker));
        Assert.Empty(await service.ListConflictsAsync(fixture.Local));
    }

    [Theory]
    [InlineData(GitConflictChoice.Local)]
    [InlineData(GitConflictChoice.Incoming)]
    [InlineData(GitConflictChoice.SaveBoth)]
    public async Task BinaryResolution_PreservesExactSelectedBytesAndOptionallyBothVersions(GitConflictChoice choice)
    {
        byte[] local = [0, 255, 1, 2, 3];
        byte[] incoming = [0, 254, 9, 8, 7];
        using var fixture = await ConflictFixture.CreateAsync("api-wrangler/fixtures/response.bin", [0, 253, 4, 5, 6], local, incoming);
        var service = new GitConflictResolutionService();
        GitConflictSnapshot snapshot = await service.ReadAsync(fixture.Local, fixture.AssetPath);
        Assert.True(snapshot.IsBinary);
        Assert.Equal(local, snapshot.LocalContent);
        Assert.Equal(incoming, snapshot.IncomingContent);
        string? alternate = choice == GitConflictChoice.SaveBoth ? "api-wrangler/fixtures/response.incoming.bin" : null;
        GitConflictPreview preview = await service.CreatePreviewAsync(snapshot, choice, saveBothPath: alternate);
        Assert.NotEmpty(preview.FinalDiff);

        string recovery = await service.ResolveAsync(preview, preview.ReviewHash, AcceptValidation);

        Assert.Equal(choice == GitConflictChoice.Incoming ? incoming : local, await File.ReadAllBytesAsync(fixture.FullPath));
        if (alternate is not null)
        {
            Assert.Equal(incoming, await File.ReadAllBytesAsync(Path.Combine(fixture.Local, alternate)));
            Assert.Contains(alternate, await GitAsync(fixture.Local, "diff", "--cached", "--name-only"), StringComparison.Ordinal);
        }
        Assert.Empty(await service.ListConflictsAsync(fixture.Local));
        Assert.True(Directory.Exists(recovery));
        Assert.Equal("unstaged unrelated work", await File.ReadAllTextAsync(Path.Combine(fixture.Local, "unrelated.txt")));
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("unrelated.txt")]
    [InlineData("api-wrangler/fixtures/response.bin")]
    public async Task SaveBoth_RejectsEscapingUnscopedOrExistingDestination(string alternate)
    {
        using var fixture = await ConflictFixture.CreateAsync("api-wrangler/fixtures/response.bin", [0, 1], [0, 2], [0, 3]);
        var service = new GitConflictResolutionService();
        GitConflictSnapshot snapshot = await service.ReadAsync(fixture.Local, fixture.AssetPath);
        byte[] before = await File.ReadAllBytesAsync(fixture.FullPath);

        Exception? error = await Record.ExceptionAsync(async () =>
        {
            GitConflictPreview preview = await service.CreatePreviewAsync(snapshot, GitConflictChoice.SaveBoth, saveBothPath: alternate);
            await service.ResolveAsync(preview, preview.ReviewHash, AcceptValidation);
        });

        Assert.NotNull(error);
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.FullPath));
        Assert.Single(await service.ListConflictsAsync(fixture.Local));
    }

    private static Task AcceptValidation(GitConflictValidationContext context, CancellationToken token) => Task.CompletedTask;

    private static async Task<string> GitAsync(string root, params string[] args)
    {
        ProcessResult result = await ProcessRunner.RunAsync("git", args, root);
        Assert.True(result.ExitCode == 0, result.StandardError);
        return result.StandardOutput.Trim();
    }

    private sealed class ConflictFixture : IDisposable
    {
        private readonly string root;
        private ConflictFixture(string root, string path)
        {
            this.root = root;
            AssetPath = path;
        }

        public string AssetPath { get; }
        public string Local => Path.Combine(root, "local clone");
        public string Incoming => Path.Combine(root, "incoming clone");
        public string FullPath => Path.Combine(Local, AssetPath);

        public static Task<ConflictFixture> CreateAsync(string path, string @base, string local, string incoming) =>
            CreateAsync(path, Encoding.UTF8.GetBytes(@base), Encoding.UTF8.GetBytes(local), Encoding.UTF8.GetBytes(incoming));

        public static async Task<ConflictFixture> CreateAsync(string path, byte[] @base, byte[] local, byte[] incoming)
        {
            string root = Path.Combine(Path.GetTempPath(), "Api Wrangler conflict acceptance", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new ConflictFixture(root, path);
            try
            {
                string remote = Path.Combine(root, "remote.git");
                await GitAsync(root, "init", "--bare", "--initial-branch=main", remote);
                await GitAsync(root, "clone", remote, fixture.Local);
                await ConfigureAsync(fixture.Local);
                Directory.CreateDirectory(Path.GetDirectoryName(fixture.FullPath)!);
                await File.WriteAllBytesAsync(fixture.FullPath, @base);
                await File.WriteAllTextAsync(Path.Combine(fixture.Local, "unrelated.txt"), "original unrelated work");
                await GitAsync(fixture.Local, "add", "--", path, "unrelated.txt");
                await GitAsync(fixture.Local, "commit", "-m", "Base assets");
                await GitAsync(fixture.Local, "push", "-u", "origin", "main");
                await GitAsync(root, "clone", remote, fixture.Incoming);
                await ConfigureAsync(fixture.Incoming);
                await File.WriteAllBytesAsync(Path.Combine(fixture.Incoming, path), incoming);
                await GitAsync(fixture.Incoming, "add", "--", path);
                await GitAsync(fixture.Incoming, "commit", "-m", "Incoming edit");
                await GitAsync(fixture.Incoming, "push");
                await File.WriteAllBytesAsync(fixture.FullPath, local);
                await GitAsync(fixture.Local, "add", "--", path);
                await GitAsync(fixture.Local, "commit", "-m", "Local edit");
                await GitAsync(fixture.Local, "fetch", "origin");
                ProcessResult merge = await ProcessRunner.RunAsync("git", ["merge", "--no-edit", "origin/main"], fixture.Local);
                Assert.NotEqual(0, merge.ExitCode);
                Assert.NotEmpty(await GitAsync(fixture.Local, "ls-files", "--unmerged"));
                await File.WriteAllTextAsync(Path.Combine(fixture.Local, "unrelated.txt"), "unstaged unrelated work");
                return fixture;
            }
            catch
            {
                fixture.Dispose();
                throw;
            }
        }

        private static async Task ConfigureAsync(string repository)
        {
            await GitAsync(repository, "config", "user.name", "Conflict Fixture");
            await GitAsync(repository, "config", "user.email", "fixture@example.test");
            await GitAsync(repository, "config", "commit.gpgsign", "false");
            await GitAsync(repository, "config", "core.autocrlf", "false");
        }

        public void Dispose()
        {
            if (!Directory.Exists(root)) return;
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }
}
