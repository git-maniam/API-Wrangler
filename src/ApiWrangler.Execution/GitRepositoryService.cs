namespace ApiWrangler.Execution;

public sealed record GitChange(string IndexStatus, string WorktreeStatus, string Path, string? OriginalPath = null);
public sealed record GitRepositoryStatus(
    string Root,
    string GitDirectory,
    string CommonGitDirectory,
    string? Branch,
    bool IsDetached,
    bool IsWorktree,
    string? Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<string> Remotes,
    IReadOnlyList<GitChange> Changes,
    bool IsMergeInProgress,
    bool IsRebaseInProgress,
    bool IsSequencerInProgress,
    bool CanWrite)
{
    public bool IsDirty => Changes.Count > 0;
    public bool HasOperationInProgress => IsMergeInProgress || IsRebaseInProgress || IsSequencerInProgress;
    public bool IsDiverged => Ahead > 0 && Behind > 0;
}

public sealed record GitCommit(string Hash, string? Parent, string Author, string Date, string Subject);
public sealed record GitToolInfo(string Executable, string Version);

/// <summary>Explicit, whole-working-tree Git operations. No operation stashes, resets, or force-pushes.</summary>
public sealed class GitRepositoryService(string executable = "git")
{
    private readonly string git = executable;

    public async Task<GitToolInfo> GetGitInfoAsync(CancellationToken token = default)
    {
        string resolved = await ResolveExecutableAsync(token).ConfigureAwait(false);
        ProcessResult result = await ProcessRunner.RunAsync(resolved, ["--version"], Environment.CurrentDirectory, token).ConfigureAwait(false);
        EnsureSuccess(result, "Git version");
        return new(resolved, result.StandardOutput.Trim());
    }

    public async Task<string> DiscoverRepositoryAsync(string path, CancellationToken token = default)
    {
        string fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath)) throw new DirectoryNotFoundException("The selected path does not exist.");
        if (File.Exists(fullPath)) fullPath = Path.GetDirectoryName(fullPath)!;
        ProcessResult result = await RunAsync(fullPath, ["rev-parse", "--show-toplevel"], token).ConfigureAwait(false);
        EnsureSuccess(result, "Repository discovery");
        return Path.GetFullPath(result.StandardOutput.Trim());
    }

    public async Task<string> InitializeAsync(string path, bool bare = false, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A repository path is required.", nameof(path));
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(fullPath);
        ProcessResult result = await RunAsync(fullPath, bare ? ["init", "--bare"] : ["init"], token).ConfigureAwait(false);
        EnsureSuccess(result, "Repository initialization");
        return bare ? fullPath : await DiscoverRepositoryAsync(fullPath, token).ConfigureAwait(false);
    }

    public async Task<string> CloneAsync(string remoteUrl, string destination, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(remoteUrl)) throw new ArgumentException("A remote URL is required.", nameof(remoteUrl));
        if (string.IsNullOrWhiteSpace(destination)) throw new ArgumentException("A clone destination is required.", nameof(destination));
        string target = Path.GetFullPath(destination);
        string? parent = Path.GetDirectoryName(target);
        if (parent is null) throw new ArgumentException("The clone destination must have a parent directory.", nameof(destination));
        Directory.CreateDirectory(parent);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            throw new IOException("Clone destination must be empty.");
        ProcessResult result = await RunAsync(parent, ["clone", "--", remoteUrl, target], token).ConfigureAwait(false);
        EnsureSuccess(result, "Clone");
        return await DiscoverRepositoryAsync(target, token).ConfigureAwait(false);
    }

    public async Task<GitRepositoryStatus> GetStatusAsync(string path, CancellationToken token = default)
    {
        string root = await DiscoverRepositoryAsync(path, token).ConfigureAwait(false);
        ProcessResult metaResult = await RunAsync(root,
            ["rev-parse", "--absolute-git-dir", "--git-common-dir", "--is-bare-repository", "--is-inside-work-tree"], token).ConfigureAwait(false);
        EnsureSuccess(metaResult, "Repository metadata");
        string[] meta = Lines(metaResult.StandardOutput);
        if (meta.Length < 4 || meta[2] == "true") throw new IOException("Bare repositories do not have a working-tree status.");
        string gitDir = Path.GetFullPath(meta[0]);
        string commonDir = Path.GetFullPath(Path.IsPathRooted(meta[1]) ? meta[1] : Path.Combine(root, meta[1]));
        ProcessResult branchResult = await RunAsync(root, ["status", "--porcelain=v1", "-z", "--branch", "--untracked-files=all"], token).ConfigureAwait(false);
        EnsureSuccess(branchResult, "Repository status");
        string[] records = branchResult.StandardOutput.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        var changes = new List<GitChange>();
        for (int i = 0; i < records.Length; i++)
        {
            string record = records[i];
            if (record.StartsWith("## ", StringComparison.Ordinal))
            {
                string header = record[3..];
                int dots = header.IndexOf("...", StringComparison.Ordinal);
                string local = dots < 0 ? header : header[..dots];
                if (dots >= 0)
                {
                    string remoteAndCounts = header[(dots + 3)..];
                    int space = remoteAndCounts.IndexOf(' ');
                    upstream = space < 0 ? remoteAndCounts : remoteAndCounts[..space];
                }
                int left = header.IndexOf("[", StringComparison.Ordinal), right = header.IndexOf("]", StringComparison.Ordinal);
                if (left >= 0 && right > left)
                {
                    foreach (string count in header[(left + 1)..right].Split(',', StringSplitOptions.TrimEntries))
                    {
                        if (count.StartsWith("ahead ", StringComparison.Ordinal) && int.TryParse(count[6..], out int a)) ahead = a;
                        if (count.StartsWith("behind ", StringComparison.Ordinal) && int.TryParse(count[7..], out int b)) behind = b;
                    }
                }
                continue;
            }
            if (record.Length < 4) continue;
            string x = record[..1], y = record.Substring(1, 1), file = record[3..];
            string? original = null;
            if (x.Contains('R') || x.Contains('C') || y.Contains('R') || y.Contains('C'))
            {
                if (i + 1 < records.Length) original = records[++i];
            }
            changes.Add(new(x, y, file, original));
        }
        ProcessResult symbolicHead = await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], token).ConfigureAwait(false);
        bool detached = symbolicHead.ExitCode != 0;
        branch = detached ? null : symbolicHead.StandardOutput.Trim();
        ProcessResult remotesResult = await RunAsync(root, ["remote"], token).ConfigureAwait(false);
        EnsureSuccess(remotesResult, "Remote listing");
        bool merge = await MarkerExistsAsync(root, "MERGE_HEAD", token).ConfigureAwait(false);
        bool rebase = await AnyMarkerExistsAsync(root, ["rebase-merge", "rebase-apply"], token).ConfigureAwait(false);
        bool sequencer = await MarkerExistsAsync(root, "sequencer", token).ConfigureAwait(false);
        bool isWorktree = !string.Equals(gitDir, commonDir, StringComparison.OrdinalIgnoreCase);
        bool canWrite = !isBareMeta(meta) && HasWriteAccess(root);
        return new(root, gitDir, commonDir, branch, detached, isWorktree, upstream, ahead, behind,
            Lines(remotesResult.StandardOutput), changes, merge, rebase, sequencer, canWrite);

        static bool isBareMeta(string[] values) => values.Length > 2 && values[2] == "true";
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(string path, CancellationToken token = default)
    {
        string root = await DiscoverRepositoryAsync(path, token).ConfigureAwait(false);
        ProcessResult result = await RunAsync(root, ["branch", "--format=%(refname:short)"], token).ConfigureAwait(false);
        EnsureSuccess(result, "Branch listing");
        return Lines(result.StandardOutput);
    }

    public async Task<IReadOnlyList<GitCommit>> GetHistoryAsync(string path, int limit = 100, CancellationToken token = default)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit), "History limit must be between 1 and 1000.");
        string root = await DiscoverRepositoryAsync(path, token).ConfigureAwait(false);
        ProcessResult result = await RunAsync(root, ["log", $"--max-count={limit}", "--format=%H%x1f%P%x1f%an%x1f%aI%x1f%s"], token).ConfigureAwait(false);
        if (result.ExitCode != 0 && !result.StandardError.Contains("does not have any commits yet", StringComparison.OrdinalIgnoreCase)) EnsureSuccess(result, "History");
        return Lines(result.StandardOutput).Select(line => line.Split('\x1f') is [var hash, var parents, var author, var date, var subject]
            ? new GitCommit(hash, parents.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), author, date, subject)
            : null).Where(x => x is not null).Cast<GitCommit>().ToArray();
    }

    public async Task<Uri> GetGitHubCompareUrlAsync(string path, string remote, string baseBranch, string headBranch, CancellationToken token = default)
    {
        EnsureName(remote, nameof(remote));
        EnsureName(baseBranch, nameof(baseBranch));
        EnsureName(headBranch, nameof(headBranch));
        string root = await DiscoverRepositoryAsync(path, token).ConfigureAwait(false);
        ProcessResult result = await RunAsync(root, ["remote", "get-url", remote], token).ConfigureAwait(false);
        EnsureSuccess(result, "Remote lookup");
        string? repositoryPath = GitHubRepositoryPath(result.StandardOutput.Trim());
        if (repositoryPath is null)
            throw new InvalidOperationException("GitHub comparison links are available for recognized github.com HTTPS and SSH remote URLs.");
        string ownerAndRepository = string.Join('/', repositoryPath.Split('/').Select(Uri.EscapeDataString));
        string EscapeBranch(string branch) => string.Join('/', branch.Split('/').Select(Uri.EscapeDataString));
        string comparison = $"{EscapeBranch(baseBranch)}...{EscapeBranch(headBranch)}";
        return new Uri($"https://github.com/{ownerAndRepository}/compare/{comparison}?expand=1");
    }

    public async Task SelectRemoteAsync(string path, string remote, string? remoteBranch = null, CancellationToken token = default)
    {
        EnsureName(remote, nameof(remote));
        string root = await DiscoverRepositoryAsync(path, token).ConfigureAwait(false);
        ProcessResult result = await RunAsync(root, ["remote", "get-url", remote], token).ConfigureAwait(false);
        EnsureSuccess(result, "Remote selection");
        string branch = await CurrentBranchAsync(root, token).ConfigureAwait(false);
        string selectedBranch = string.IsNullOrWhiteSpace(remoteBranch) ? branch : remoteBranch;
        EnsureName(selectedBranch, nameof(remoteBranch));
        ProcessResult set = await RunAsync(root, ["config", "branch." + branch + ".remote", remote], token).ConfigureAwait(false);
        EnsureSuccess(set, "Remote selection");
        ProcessResult merge = await RunAsync(root, ["config", "branch." + branch + ".merge", "refs/heads/" + selectedBranch], token).ConfigureAwait(false);
        EnsureSuccess(merge, "Remote branch selection");
    }

    public async Task AddRemoteAsync(string path, string name, string url, CancellationToken token = default)
    {
        EnsureName(name, nameof(name));
        if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("A remote URL is required.", nameof(url));
        string root = await DiscoverRepositoryAsync(path, token).ConfigureAwait(false);
        ProcessResult result = await RunAsync(root, ["remote", "add", name, url], token).ConfigureAwait(false);
        EnsureSuccess(result, "Adding remote");
    }

    public async Task FetchAsync(string path, string remote, CancellationToken token = default)
    {
        EnsureName(remote, nameof(remote));
        string root = await DiscoverRepositoryAsync(path, token).ConfigureAwait(false);
        ProcessResult result = await RunAsync(root, ["fetch", "--no-recurse-submodules", remote], token).ConfigureAwait(false);
        EnsureSuccess(result, "Fetch");
    }

    public async Task PushAsync(string path, string remote, CancellationToken token = default)
    {
        EnsureName(remote, nameof(remote));
        string root = await DiscoverRepositoryAsync(path, token).ConfigureAwait(false);
        GitRepositoryStatus status = await GetStatusAsync(root, token).ConfigureAwait(false);
        EnsureNoOperation(status);
        if (status.IsDetached || status.Branch is null) throw new InvalidOperationException("Push requires a branch; detached HEAD is not pushed implicitly.");
        ProcessResult result = await RunAsync(root, ["push", remote, status.Branch], token).ConfigureAwait(false);
        EnsureSuccess(result, "Push");
    }

    public async Task CreateBranchAsync(string path, string branch, string? startPoint = null, CancellationToken token = default)
    {
        EnsureName(branch, nameof(branch));
        string root = await DiscoverRepositoryAsync(path, token).ConfigureAwait(false);
        await EnsureSafeWholeTreeAsync(root, token).ConfigureAwait(false);
        if (startPoint is not null) EnsureRevision(startPoint, nameof(startPoint));
        string[] args = startPoint is null ? ["switch", "--create", branch] : ["switch", "--create", branch, startPoint];
        ProcessResult result = await RunAsync(root, args, token).ConfigureAwait(false);
        EnsureSuccess(result, "Branch creation");
    }

    public async Task SwitchBranchAsync(string path, string branch, CancellationToken token = default)
    {
        EnsureName(branch, nameof(branch));
        string root = await DiscoverRepositoryAsync(path, token).ConfigureAwait(false);
        await EnsureSafeWholeTreeAsync(root, token).ConfigureAwait(false);
        ProcessResult result = await RunAsync(root, ["switch", branch], token).ConfigureAwait(false);
        EnsureSuccess(result, "Branch switch");
    }

    public async Task FastForwardAsync(string path, CancellationToken token = default)
    {
        string root = await DiscoverRepositoryAsync(path, token).ConfigureAwait(false);
        GitRepositoryStatus status = await GetStatusAsync(root, token).ConfigureAwait(false);
        EnsureClean(status);
        EnsureNoOperation(status);
        if (status.IsDetached || string.IsNullOrWhiteSpace(status.Upstream)) throw new InvalidOperationException("Fast-forward requires a branch with an upstream.");
        if (status.Ahead > 0) throw new InvalidOperationException("Local commits exist; choose explicit merge or rebase integration.");
        if (status.Behind == 0) return;
        ProcessResult result = await RunAsync(root, ["merge", "--ff-only", "@{upstream}"], token).ConfigureAwait(false);
        EnsureSuccess(result, "Fast-forward");
    }

    public async Task MergeAsync(string path, string revision, CancellationToken token = default) => await IntegrateAsync(path, "merge", revision, token).ConfigureAwait(false);
    public async Task RebaseAsync(string path, string revision, CancellationToken token = default) => await IntegrateAsync(path, "rebase", revision, token).ConfigureAwait(false);

    public async Task RestoreAssetAsync(string path, string revision, string assetPath, CancellationToken token = default)
    {
        EnsureRevision(revision, nameof(revision));
        string root = await DiscoverRepositoryAsync(path, token).ConfigureAwait(false);
        string relative = ValidateProjectAssetPath(root, assetPath);
        GitRepositoryStatus status = await GetStatusAsync(root, token).ConfigureAwait(false);
        EnsureNoOperation(status);
        StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (status.Changes.Any(c => string.Equals(c.Path, relative, pathComparison) || string.Equals(c.OriginalPath, relative, pathComparison)))
            throw new InvalidOperationException("The selected asset has local changes. Save or commit them before restoring from history.");
        ProcessResult result = await RunAsync(root, ["restore", $"--source={revision}", "--worktree", "--", relative], token).ConfigureAwait(false);
        EnsureSuccess(result, "Asset restore");
    }

    private async Task IntegrateAsync(string path, string operation, string revision, CancellationToken token)
    {
        EnsureRevision(revision, nameof(revision));
        string root = await DiscoverRepositoryAsync(path, token).ConfigureAwait(false);
        await EnsureSafeWholeTreeAsync(root, token).ConfigureAwait(false);
        ProcessResult result = await RunAsync(root, [operation, revision], token).ConfigureAwait(false);
        EnsureSuccess(result, operation);
    }

    private async Task EnsureSafeWholeTreeAsync(string root, CancellationToken token)
    {
        GitRepositoryStatus status = await GetStatusAsync(root, token).ConfigureAwait(false);
        EnsureClean(status);
        EnsureNoOperation(status);
        if (!status.CanWrite) throw new UnauthorizedAccessException("Repository working tree is not writable.");
    }

    private static void EnsureClean(GitRepositoryStatus status)
    {
        if (status.IsDirty) throw new InvalidOperationException("The whole Git working tree has changes. Save or commit them before this operation; no automatic stash is performed.");
    }

    private static void EnsureNoOperation(GitRepositoryStatus status)
    {
        if (status.HasOperationInProgress) throw new InvalidOperationException("A merge, rebase, or sequencer operation is already in progress. Resolve or abort it in Git before starting another operation.");
    }

    private async Task<string> CurrentBranchAsync(string root, CancellationToken token)
    {
        GitRepositoryStatus status = await GetStatusAsync(root, token).ConfigureAwait(false);
        if (status.IsDetached || status.Branch is null) throw new InvalidOperationException("Detached HEAD has no branch remote selection.");
        return status.Branch;
    }

    private async Task<bool> MarkerExistsAsync(string root, string marker, CancellationToken token)
    {
        ProcessResult result = await RunAsync(root, ["rev-parse", "--git-path", marker], token).ConfigureAwait(false);
        EnsureSuccess(result, "Git operation state");
        string value = result.StandardOutput.Trim();
        return File.Exists(Path.IsPathRooted(value) ? value : Path.Combine(root, value)) || Directory.Exists(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
    }

    private async Task<bool> AnyMarkerExistsAsync(string root, IEnumerable<string> markers, CancellationToken token)
    {
        foreach (string marker in markers) if (await MarkerExistsAsync(root, marker, token).ConfigureAwait(false)) return true;
        return false;
    }

    private static bool HasWriteAccess(string root)
    {
        try
        {
            string probe = Path.Combine(root, ".api-wrangler-write-check-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private string ValidateProjectAssetPath(string root, string assetPath)
    {
        if (string.IsNullOrWhiteSpace(assetPath)) throw new ArgumentException("A project asset path is required.", nameof(assetPath));
        string projectRoot = Path.Combine(root, "api-wrangler");
        string full = Path.GetFullPath(Path.IsPathRooted(assetPath) ? assetPath : Path.Combine(root, assetPath));
        string relativeToRoot = Path.GetRelativePath(root, full);
        if (relativeToRoot == "." || relativeToRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relativeToRoot))
            throw new ArgumentException("The selected path is outside the repository.", nameof(assetPath));
        string relativeToProject = Path.GetRelativePath(projectRoot, full);
        if (relativeToProject.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativeToProject))
            throw new ArgumentException("Restore is limited to assets under api-wrangler/.", nameof(assetPath));
        return relativeToRoot.Replace(Path.DirectorySeparatorChar, '/');
    }

    private async Task<string> ResolveExecutableAsync(CancellationToken token)
    {
        if (Path.IsPathRooted(git) || git.Contains(Path.DirectorySeparatorChar) || git.Contains(Path.AltDirectorySeparatorChar)) return Path.GetFullPath(git);
        string finder = OperatingSystem.IsWindows() ? "where.exe" : "which";
        ProcessResult result = await ProcessRunner.RunAsync(finder, [git], Environment.CurrentDirectory, token).ConfigureAwait(false);
        EnsureSuccess(result, "Git discovery");
        return Lines(result.StandardOutput).FirstOrDefault() ?? throw new FileNotFoundException("Git executable was not found.");
    }

    private static string? GitHubRepositoryPath(string remote)
    {
        string? path = null;
        if (Uri.TryCreate(remote, UriKind.Absolute, out Uri? uri) &&
            uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            uri.Scheme is "https" or "http" or "ssh")
        {
            path = uri.AbsolutePath.Trim('/');
        }
        else
        {
            var match = System.Text.RegularExpressions.Regex.Match(remote,
                @"^(?:[^@/:]+@)?github\.com:(?<path>[^\s]+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (match.Success) path = match.Groups["path"].Value.Trim('/');
        }

        if (path is null) return null;
        string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return null;
        string repo = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(repo) ? null : $"{parts[0]}/{repo}";
    }

    private Task<ProcessResult> RunAsync(string workingDirectory, string[] args, CancellationToken token) => ProcessRunner.RunAsync(git, args, workingDirectory, token);
    private static string[] Lines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static void EnsureName(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal) || value.Any(char.IsControl))
            throw new ArgumentException("A valid Git name is required.", parameter);
    }
    private static void EnsureRevision(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("-", StringComparison.Ordinal) || value.Any(char.IsControl))
            throw new ArgumentException("A valid Git revision is required.", parameter);
    }
    private static void EnsureSuccess(ProcessResult result, string operation)
    {
        if (result.ExitCode != 0) throw new IOException($"{operation} failed (git exit {result.ExitCode}): {Sanitize(result.StandardError)}");
    }
    private static string Sanitize(string value)
    {
        string flattened = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        flattened = System.Text.RegularExpressions.Regex.Replace(flattened, "(?i)(https?://)[^/@\\s]+@", "$1[redacted]@");
        return flattened.Length <= 512 ? flattened : flattened[..512];
    }
}
