namespace ApiWrangler.Execution;

public sealed class GitProbe(string executable = "git")
{
    public Task<ProcessResult> StatusAsync(string repository, CancellationToken cancellationToken = default) =>
        ProcessRunner.RunAsync(executable, ["--no-optional-locks", "status", "--porcelain=v1", "--branch"], repository, cancellationToken);

    // Fetch is deliberately separate from integration. Inherit the user's Git authentication configuration.
    public Task<ProcessResult> FetchAsync(string repository, CancellationToken cancellationToken = default) =>
        ProcessRunner.RunAsync(executable, ["fetch", "--no-recurse-submodules", "origin"], repository, cancellationToken);
}
