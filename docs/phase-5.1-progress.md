# Phase 5.1 progress

Implementation and verification checkpoint: 2026-11-08 19:25 IST, branch `phase5-repository-workflows`. P5 #22 and P5.1 #23 remain In progress. P5.2 #24 has also started In progress because it depends on Phase 4 (#19), not P5.1.

## Implemented in this checkpoint

- A Git executable service for repository discovery, init/clone, status facts, remotes, fetch/push, branches, history, fast-forward, explicit merge/rebase, and restore of paths under `api-wrangler/`.
- A desktop Git collaboration tab for the same operations. The executable path is visible and editable; its version is shown after refresh.
- Added `scripts/Test-GitDesktop.ps1` and a `--git-smoke` desktop diagnostic. It exercises clone/init/open, remote selection, fetch, branch operations, push, history, P5.2 schema validation and exact-diff commit review, commit, and scoped restore against a local bare remote. The screenshot was inspected at `artifacts/git-desktop-smoke/git-workbench.png`.
- Added GitHub compare URL construction for recognized github.com HTTPS/SSH remotes using Git's effective remote URL (including `insteadOf` rewrites), plus a visible Compare action and visible labels for Git inputs.
- Fixed a Windows case-insensitive restore guard that could otherwise overwrite a dirty asset when the user typed different path casing.
- Whole-tree safety guards for branch changes and integration. Fetch updates refs only. Push is non-forced. No operation runs stash, hard reset, or automatic integration.
- Git diagnostics are bounded and HTTPS URL user information is redacted.

## Executed verification

- `dotnet build ApiWrangler.slnx -c Release --no-restore` — passed, 0 warnings and 0 errors.
- `dotnet test tests/ApiWrangler.IntegrationTests/ApiWrangler.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~GitRepository_|FullyQualifiedName~GitBranchChange_|FullyQualifiedName~GitSecretScannerTests|FullyQualifiedName~GitCommitReviewServiceTests" --logger "console;verbosity=minimal"` — 12 passed, 0 failed, 0 skipped.
- `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-GitDesktop.ps1` — passed, including reviewed commit validation and push.

The tests use local bare remotes and cover fetch immutability, ahead/behind and dirty facts, unrelated dirt, rejected push preservation, offline fetch, fast-forward, branch create/switch/upstream, history, scoped restore including path-case mismatch, GitHub compare links, detached HEAD, worktrees, merge/rebase conflict recovery, external branch changes, snapshots, and `index.lock` write suspension.

## Remaining acceptance

- Exercise rejected authentication through a configured desktop credential/SSH flow; a local HTTP 401 fixture hung in Git's credential path and was removed, so it is not reported as passed.
- Manual desktop review remains for accessibility and provider-specific credential prompts; deterministic smoke covers clone/open/init/remote and Git actions.
- P5.1 remains In progress. P5.2 is now In progress with scoped review/commit implementation. P5.3 semantic conflict resolution remains ahead of Phase 5 exit review.
