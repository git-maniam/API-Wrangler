using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using ApiWrangler.Execution;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiWrangler.Desktop;

public partial class GitWorkbenchViewModel : ObservableObject
{
    private GitRepositoryService Git => new(GitExecutable);

    [ObservableProperty] private string gitExecutable = "git";
    [ObservableProperty] private string gitVersion = "";
    [ObservableProperty] private string repositoryPath = "";
    [ObservableProperty] private string remoteName = "origin";
    [ObservableProperty] private string remoteUrl = "";
    [ObservableProperty] private string branchName = "";
    [ObservableProperty] private string revision = "";
    [ObservableProperty] private string assetPath = "";
    [ObservableProperty] private string summary = "Open a local Git repository or clone one to begin.";
    [ObservableProperty] private string operationStatus = "";

    public ObservableCollection<string> Changes { get; } = [];
    public ObservableCollection<string> Branches { get; } = [];
    public ObservableCollection<GitCommit> History { get; } = [];

    [RelayCommand]
    private Task RefreshAsync(CancellationToken token) => RunAsync(() => RefreshCoreAsync(token));

    [RelayCommand]
    private Task InitializeAsync(CancellationToken token) => RunAsync(async () =>
    {
        RepositoryPath = await Git.InitializeAsync(RepositoryPath, token: token);
        await RefreshCoreAsync(token);
        OperationStatus = "Initialized a local repository. No files were committed.";
    });

    [RelayCommand]
    private Task CloneAsync(CancellationToken token) => RunAsync(async () =>
    {
        RepositoryPath = await Git.CloneAsync(RemoteUrl, RepositoryPath, token);
        await RefreshCoreAsync(token);
        OperationStatus = "Clone complete.";
    });

    [RelayCommand]
    private Task FetchAsync(CancellationToken token) => RunAsync(async () =>
    {
        await Git.FetchAsync(RepositoryPath, RemoteName, token);
        await RefreshCoreAsync(token);
        OperationStatus = "Fetch complete. Working files were not changed.";
    });

    [RelayCommand]
    private Task PushAsync(CancellationToken token) => RunAsync(async () =>
    {
        await Git.PushAsync(RepositoryPath, RemoteName, token);
        await RefreshCoreAsync(token);
        OperationStatus = "Push complete.";
    });

    [RelayCommand]
    private Task AddRemoteAsync(CancellationToken token) => RunAsync(async () =>
    {
        await Git.AddRemoteAsync(RepositoryPath, RemoteName, RemoteUrl, token);
        await RefreshCoreAsync(token);
        OperationStatus = $"Remote '{RemoteName}' added.";
    });

    [RelayCommand]
    private Task SelectRemoteAsync(CancellationToken token) => RunAsync(async () =>
    {
        await Git.SelectRemoteAsync(RepositoryPath, RemoteName, token: token);
        await RefreshCoreAsync(token);
        OperationStatus = $"Remote '{RemoteName}' selected for the current branch.";
    });

    [RelayCommand]
    private Task CreateBranchAsync(CancellationToken token) => RunAsync(async () =>
    {
        await Git.CreateBranchAsync(RepositoryPath, BranchName, token: token);
        await RefreshCoreAsync(token);
        OperationStatus = $"Created and switched to '{BranchName}'.";
    });

    [RelayCommand]
    private Task SwitchBranchAsync(CancellationToken token) => RunAsync(async () =>
    {
        await Git.SwitchBranchAsync(RepositoryPath, BranchName, token);
        await RefreshCoreAsync(token);
        OperationStatus = $"Switched to '{BranchName}'.";
    });

    [RelayCommand]
    private Task FastForwardAsync(CancellationToken token) => RunAsync(async () =>
    {
        await Git.FastForwardAsync(RepositoryPath, token);
        await RefreshCoreAsync(token);
        OperationStatus = "Fast-forward complete.";
    });

    [RelayCommand]
    private Task MergeAsync(CancellationToken token) => RunAsync(async () =>
    {
        await Git.MergeAsync(RepositoryPath, Revision, token);
        await RefreshCoreAsync(token);
        OperationStatus = "Merge complete.";
    });

    [RelayCommand]
    private Task RebaseAsync(CancellationToken token) => RunAsync(async () =>
    {
        await Git.RebaseAsync(RepositoryPath, Revision, token);
        await RefreshCoreAsync(token);
        OperationStatus = "Rebase complete.";
    });

    [RelayCommand]
    private Task RestoreAsync(CancellationToken token) => RunAsync(async () =>
    {
        await Git.RestoreAssetAsync(RepositoryPath, Revision, AssetPath, token);
        await RefreshCoreAsync(token);
        OperationStatus = $"Restored '{AssetPath}' to the selected revision.";
    });

    [RelayCommand]
    private Task LoadHistoryAsync(CancellationToken token) => RunAsync(async () =>
    {
        History.Clear();
        foreach (GitCommit commit in await Git.GetHistoryAsync(RepositoryPath, token: token)) History.Add(commit);
        OperationStatus = $"Loaded {History.Count} commits.";
    });

    private async Task RefreshCoreAsync(CancellationToken token)
    {
        GitToolInfo tool = await Git.GetGitInfoAsync(token);
        GitExecutable = tool.Executable;
        GitVersion = tool.Version;
        RepositoryPath = await Git.DiscoverRepositoryAsync(RepositoryPath, token);
        GitRepositoryStatus state = await Git.GetStatusAsync(RepositoryPath, token);
        Changes.Clear();
        foreach (GitChange change in state.Changes) Changes.Add($"{change.IndexStatus}{change.WorktreeStatus}  {change.Path}");
        Branches.Clear();
        foreach (string branch in await Git.ListBranchesAsync(RepositoryPath, token)) Branches.Add(branch);
        BranchName = state.Branch ?? "";
        Summary = $"Git {GitVersion} ({GitExecutable})\n{state.Root}\nBranch: {(state.IsDetached ? "detached HEAD" : state.Branch)}    Upstream: {state.Upstream ?? "none"}    Ahead: {state.Ahead}    Behind: {state.Behind}\nRemotes: {(state.Remotes.Count == 0 ? "none" : string.Join(", ", state.Remotes))}    {(state.IsWorktree ? "Linked worktree" : "Main worktree")}    {(state.CanWrite ? "Writable" : "Read-only")}\n{(state.HasOperationInProgress ? "Git operation in progress" : "No Git operation in progress")}";
        OperationStatus = state.IsDirty ? "Working tree has changes. Branch switching and integration remain blocked until the whole tree is clean." : "Working tree is clean.";
    }

    private async Task RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { OperationStatus = "Git operation cancelled."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception)
        {
            OperationStatus = error.Message;
        }
    }
}
