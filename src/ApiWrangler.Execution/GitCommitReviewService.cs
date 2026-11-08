using System.Text.Json;
using System.Diagnostics;

namespace ApiWrangler.Execution;

public sealed record GitCommitReview(string RepositoryRoot, IReadOnlyList<string> SelectedPaths,
    string CachedDiff, string IndexTreeHash, IReadOnlyList<GitSecretFinding> SecretFindings,
    IReadOnlyList<string> JsonSyntaxErrors, bool SchemaValidated, string? BaseCommit, string? BaseBranch)
{
    /// <summary>Schema checks are supplied by the caller; JSON syntax checks run here.</summary>
    public string SchemaValidationNote => SchemaValidated ? "Project schema validation passed." : "Project schema validation was not performed.";
    public bool CanCommit => SecretFindings.Count == 0 && JsonSyntaxErrors.Count == 0;
}

/// <summary>Stages and reviews only explicit project asset paths before a caller-confirmed commit.</summary>
public sealed class GitCommitReviewService(string executable = "git")
{
    private const int MaxCachedDiffCharacters = 4 * 1024 * 1024;
    private readonly string git = executable;

    public async Task<GitCommitReview> ReviewAsync(string repositoryPath, IEnumerable<string> selectedPaths,
        CancellationToken cancellationToken = default,
        Func<IReadOnlyList<string>, CancellationToken, Task>? validateSelectedProjectAssets = null)
    {
        string root = await new GitRepositoryService(git).DiscoverRepositoryAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
        string[] paths = selectedPaths.Select(ValidatePath).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length == 0) throw new ArgumentException("Select at least one project asset path.", nameof(selectedPaths));
        GitRepositoryStatus initialState = await new GitRepositoryService(git).GetStatusAsync(root, cancellationToken).ConfigureAwait(false);
        EnsureCommitState(initialState);
        string? baseCommit = await ReadHeadAsync(root, cancellationToken).ConfigureAwait(false);

        // This check precedes every index-mutating command.
        string[] stagedBefore = await ReadStagedPathsAsync(root, cancellationToken).ConfigureAwait(false);
        if (stagedBefore.Any(path => !paths.Contains(path, StringComparer.Ordinal)))
            throw new InvalidOperationException("The index contains paths outside this review selection.");
        if (validateSelectedProjectAssets is not null)
            await validateSelectedProjectAssets(paths, cancellationToken).ConfigureAwait(false);

        await RunCheckedAsync(root, ["add", "-A", "--", .. paths], "Staging selected assets", cancellationToken).ConfigureAwait(false);
        string[] staged = await ReadStagedPathsAsync(root, cancellationToken).ConfigureAwait(false);
        if (staged.Any(path => !paths.Contains(path, StringComparer.Ordinal)))
            throw new InvalidOperationException("The index contains paths outside this review selection.");

        var findings = new List<GitSecretFinding>();
        var jsonErrors = new List<string>();
        foreach (string path in staged)
        {
            byte[]? bytes = await ReadStagedBlobAsync(root, path, cancellationToken).ConfigureAwait(false);
            if (bytes is null) continue; // Deleted paths have no cached blob.
            findings.AddRange(GitSecretScanner.Scan(path, bytes));
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try { using JsonDocument _ = JsonDocument.Parse(bytes); }
                catch (JsonException exception) { jsonErrors.Add($"{path}: invalid JSON syntax at line {exception.LineNumber + 1}."); }
            }
        }

        ProcessResult diff = await RunAsync(root, ["diff", "--cached", "--no-ext-diff", "--binary", "--"],
            cancellationToken, MaxCachedDiffCharacters).ConfigureAwait(false);
        EnsureSuccess(diff, "Cached diff");
        string tree = (await RunCheckedAsync(root, ["write-tree"], "Index tree", cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        GitRepositoryStatus reviewedState = await new GitRepositoryService(git).GetStatusAsync(root, cancellationToken).ConfigureAwait(false);
        EnsureCommitState(reviewedState);
        string? reviewedHead = await ReadHeadAsync(root, cancellationToken).ConfigureAwait(false);
        if (reviewedState.Branch != initialState.Branch || reviewedHead != baseCommit)
            throw new InvalidOperationException("The repository branch or HEAD changed while the selected files were reviewed.");
        return new(root, paths, diff.StandardOutput, tree, findings, jsonErrors,
            validateSelectedProjectAssets is not null, baseCommit, initialState.Branch);
    }

    public async Task<string> CommitAsync(GitCommitReview review, string message, string confirmedIndexTreeHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (!review.CanCommit) throw new InvalidOperationException("Review findings must be resolved before committing.");
        if (string.IsNullOrWhiteSpace(message) || message.Any(char.IsControl)) throw new ArgumentException("A one-line commit message is required.", nameof(message));
        GitRepositoryStatus currentState = await new GitRepositoryService(git).GetStatusAsync(review.RepositoryRoot, cancellationToken).ConfigureAwait(false);
        EnsureCommitState(currentState);
        string? currentHead = await ReadHeadAsync(review.RepositoryRoot, cancellationToken).ConfigureAwait(false);
        if (currentState.Branch != review.BaseBranch || currentHead != review.BaseCommit)
            throw new InvalidOperationException("The repository branch or HEAD changed after review. Review the selected files again.");
        string currentTree = (await RunCheckedAsync(review.RepositoryRoot, ["write-tree"], "Index tree", cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
        if (!string.Equals(currentTree, review.IndexTreeHash, StringComparison.Ordinal) ||
            !string.Equals(currentTree, confirmedIndexTreeHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The index tree differs from the reviewed and caller-confirmed tree.");

        ProcessResult commit = await RunAsync(review.RepositoryRoot, ["commit", "-m", message], cancellationToken).ConfigureAwait(false);
        EnsureSuccess(commit, "Commit");
        return (await RunCheckedAsync(review.RepositoryRoot, ["rev-parse", "HEAD"], "Commit hash", cancellationToken).ConfigureAwait(false)).StandardOutput.Trim();
    }

    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Selected paths must be relative project asset paths.");
        string normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || Path.IsPathRooted(path) || normalized.Split('/').Any(part => part is ".." or ".") ||
            !normalized.StartsWith("api-wrangler/", StringComparison.Ordinal) || normalized.Length <= "api-wrangler/".Length)
            throw new ArgumentException("Selected paths must be files beneath repository-root api-wrangler/.", nameof(path));
        return normalized;
    }

    private async Task<string[]> ReadStagedPathsAsync(string root, CancellationToken token)
    {
        ProcessResult result = await RunCheckedAsync(root, ["diff", "--cached", "--name-only", "--no-renames", "-z"], "Staged path listing", token).ConfigureAwait(false);
        return result.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task<ProcessResult> RunCheckedAsync(string root, string[] args, string operation, CancellationToken token)
    {
        ProcessResult result = await RunAsync(root, args, token).ConfigureAwait(false);
        EnsureSuccess(result, operation);
        return result;
    }

    private async Task<string?> ReadHeadAsync(string root, CancellationToken token)
    {
        ProcessResult result = await RunAsync(root, ["rev-parse", "--verify", "HEAD"], token).ConfigureAwait(false);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    private static void EnsureCommitState(GitRepositoryStatus state)
    {
        if (state.IsDetached || state.Branch is null)
            throw new InvalidOperationException("Create or switch to a branch before reviewing a commit.");
        if (state.HasOperationInProgress)
            throw new InvalidOperationException("Resolve the active Git integration operation before reviewing a commit.");
    }

    private Task<ProcessResult> RunAsync(string root, string[] args, CancellationToken token,
        int maxOutputCharacters = ProcessRunner.DefaultMaxOutputCharacters) =>
        ProcessRunner.RunAsync(git, args, root, token, maxOutputCharacters);

    private async Task<byte[]?> ReadStagedBlobAsync(string root, string path, CancellationToken token)
    {
        var start = new ProcessStartInfo(git) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("show");
        start.ArgumentList.Add(":" + path);
        using Process process = Process.Start(start) ?? throw new IOException("Git could not read a staged asset.");
        using var bytes = new MemoryStream();
        Task output = process.StandardOutput.BaseStream.CopyToAsync(bytes, token);
        Task<string> error = process.StandardError.ReadToEndAsync(token);
        string diagnostic;
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await output.ConfigureAwait(false);
            diagnostic = await error.ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await output.ConfigureAwait(false); } catch { }
            try { await error.ConfigureAwait(false); } catch { }
            throw;
        }
        if (process.ExitCode != 0)
        {
            ProcessResult indexEntry = await RunAsync(root, ["ls-files", "--stage", "-z", "--", path], token).ConfigureAwait(false);
            EnsureSuccess(indexEntry, "Checking staged asset");
            if (indexEntry.StandardOutput.Length == 0) return null; // Git confirms a staged deletion has no index object.
            throw new IOException($"Reading staged asset failed (git exit {process.ExitCode}): {diagnostic.Trim()}");
        }
        return bytes.ToArray();
    }

    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0) throw new IOException($"{operation} failed (git exit {result.ExitCode}): {result.StandardError.Trim()}");
    }
}
