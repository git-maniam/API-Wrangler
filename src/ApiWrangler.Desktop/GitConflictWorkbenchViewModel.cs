using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using ApiWrangler.Execution;
using ApiWrangler.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiWrangler.Desktop;

public partial class GitConflictWorkbenchViewModel(GitWorkbenchViewModel workbench) : ObservableObject
{
    private GitConflictResolutionService Service => new(workbench.GitExecutable);
    private GitConflictSnapshot? snapshot;
    private GitConflictPreview? preview;
    private long revision;

    public ObservableCollection<string> Paths { get; } = [];
    [ObservableProperty] private string? selectedPath;
    [ObservableProperty] private string baseText = "";
    [ObservableProperty] private string localText = "";
    [ObservableProperty] private string incomingText = "";
    [ObservableProperty] private string draftText = "";
    [ObservableProperty] private string saveBothPath = "";
    [ObservableProperty] private string finalDiff = "";
    [ObservableProperty] private string conflictDetails = "";
    [ObservableProperty] private string recoveryPath = "";
    [ObservableProperty] private string status = "Load unresolved Git paths, then select a path and load its three versions.";
    [ObservableProperty] private bool reviewAccepted;
    [ObservableProperty] private bool isBinary;

    public bool CanResolve => preview is not null && ReviewAccepted;
    public bool CanEdit => snapshot is not null && !IsBinary;

    [RelayCommand]
    private Task RefreshAsync(CancellationToken token) => RunAsync(async () =>
    {
        Reset();
        foreach (string path in await Service.ListConflictsAsync(workbench.RepositoryPath, token)) Paths.Add(path);
        Status = Paths.Count == 0 ? "Git reports no unresolved project asset paths." : $"{Paths.Count} unresolved path(s). Select and load a path.";
    });

    [RelayCommand]
    private Task LoadAsync(CancellationToken token) => RunAsync(async () =>
    {
        string path = SelectedPath ?? throw new InvalidOperationException("Select an unresolved path first.");
        ClearSnapshot();
        long loadingRevision = revision;
        GitConflictSnapshot loaded = await Service.ReadAsync(workbench.RepositoryPath, path, token);
        if (loadingRevision != revision || SelectedPath != path) return;
        snapshot = loaded;
        IsBinary = loaded.IsBinary;
        BaseText = DescribeVersion(loaded.BaseText, loaded.BaseContent);
        LocalText = DescribeVersion(loaded.LocalText, loaded.LocalContent);
        IncomingText = DescribeVersion(loaded.IncomingText, loaded.IncomingContent);
        DraftText = loaded.ProposalText ?? loaded.LocalText ?? loaded.IncomingText ?? "";
        ConflictDetails = loaded.Conflicts.Count == 0
            ? "No JSON field conflicts were reported. Review the complete result before resolving."
            : string.Join(Environment.NewLine, loaded.Conflicts.Select(item => $"{item.Path}: {item.Reason}"));
        OnPropertyChanged(nameof(CanEdit));
        Status = loaded.IsBinary
            ? "Binary content: choose a version or save both. Git stages are shown as local (stage 2) and incoming (stage 3); during rebase these follow Git's stage meanings."
            : "Edit the proposed result or choose a version. During rebase, local is Git stage 2 and incoming is stage 3.";
    });

    [RelayCommand]
    private Task ReviewEditedAsync(CancellationToken token) => ReviewAsync(GitConflictChoice.EditedText, token);
    [RelayCommand]
    private Task UseLocalAsync(CancellationToken token) => ReviewAsync(GitConflictChoice.Local, token);
    [RelayCommand]
    private Task UseIncomingAsync(CancellationToken token) => ReviewAsync(GitConflictChoice.Incoming, token);
    [RelayCommand]
    private Task DeleteAsync(CancellationToken token) => ReviewAsync(GitConflictChoice.Delete, token);
    [RelayCommand]
    private Task SaveBothAsync(CancellationToken token) => ReviewAsync(GitConflictChoice.SaveBoth, token);

    private Task ReviewAsync(GitConflictChoice choice, CancellationToken token) => RunAsync(async () =>
    {
        GitConflictSnapshot loaded = snapshot ?? throw new InvalidOperationException("Load a conflict first.");
        InvalidateReview();
        long reviewingRevision = revision;
        GitConflictPreview candidate = await Service.CreatePreviewAsync(loaded, choice, DraftText, SaveBothPath, token);
        await ValidateAsync(new(candidate.Snapshot.RepositoryRoot, candidate.Snapshot.Path,
            candidate.Content, candidate.AdditionalPath, candidate.AdditionalContent), token);
        if (reviewingRevision != revision || snapshot != loaded) return;
        preview = candidate;
        FinalDiff = candidate.FinalDiff;
        OnPropertyChanged(nameof(CanResolve));
        Status = "Schema/reference validation passed. Review the final diff, acknowledge your decision, then mark this path resolved.";
    });

    [RelayCommand]
    private Task ResolveAsync(CancellationToken token) => RunAsync(async () =>
    {
        GitConflictPreview reviewed = preview ?? throw new InvalidOperationException("Review a result first.");
        if (!CanResolve) throw new InvalidOperationException("Acknowledge the conflict decisions and final diff first.");
        string recovery = await Service.ResolveAsync(reviewed, reviewed.ReviewHash, ValidateAsync, token);
        RecoveryPath = recovery;
        Paths.Remove(reviewed.Snapshot.Path);
        SelectedPath = null;
        ClearSnapshot();
        Status = "Path marked resolved. Recovery copies are retained. Refresh Git status; complete the merge or rebase separately after all conflicts are resolved.";
        workbench.OperationStatus = Status;
    });

    private static async Task ValidateAsync(GitConflictValidationContext context, CancellationToken token)
    {
        const string prefix = "api-wrangler/";
        var files = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        Add(context.Path, context.Content);
        if (context.AdditionalPath is not null) Add(context.AdditionalPath, context.AdditionalContent);
        var view = await ProjectProposalValidator.ValidateAsync(Path.Combine(context.RepositoryRoot, "api-wrangler"), files, token);
        if (!view.IsValid)
            throw new InvalidOperationException("Resolution validation failed: " + string.Join("; ", view.Diagnostics.Take(5)
                .Select(item => $"{item.File}{item.Pointer}: {item.Message}"))
                + " Other conflicted definitions must also be valid; resolve them together using external Git tools if they depend on one another.");

        void Add(string path, byte[]? bytes)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidOperationException("Conflict resolution is limited to the repository-root api-wrangler/ project.");
            files.Add(path[prefix.Length..], bytes);
        }
    }

    private Task RunAsync(Func<Task> action) => workbench.RunAsync(async () =>
    {
        try { await action(); }
        catch (OperationCanceledException) { Status = "Conflict operation cancelled."; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or Win32Exception or System.Text.Json.JsonException)
        {
            InvalidateReview();
            Status = error.Message;
        }
    });

    internal void Reset()
    {
        Paths.Clear();
        SelectedPath = null;
        ClearSnapshot();
    }

    private void ClearSnapshot()
    {
        snapshot = null;
        BaseText = LocalText = IncomingText = DraftText = ConflictDetails = "";
        IsBinary = false;
        InvalidateReview();
        OnPropertyChanged(nameof(CanEdit));
    }

    private void InvalidateReview()
    {
        revision++;
        preview = null;
        FinalDiff = "";
        ReviewAccepted = false;
        OnPropertyChanged(nameof(CanResolve));
    }

    private static string DescribeVersion(string? text, byte[]? bytes) => text ??
        (bytes is null ? "[Absent in this version]" : $"[Binary content: {bytes.Length:N0} bytes]");

    partial void OnSelectedPathChanged(string? value) => ClearSnapshot();
    partial void OnDraftTextChanged(string value) => InvalidateReview();
    partial void OnSaveBothPathChanged(string value) => InvalidateReview();
    partial void OnReviewAcceptedChanged(bool value) => OnPropertyChanged(nameof(CanResolve));
}
