# GitHub implementation tracking

Kanban: https://github.com/users/git-maniam/projects/4/views/3

Repository issues carry acceptance criteria and native parent/sub-issue relationships. The project supplies Status, Phase, Priority, Size, and Milestone fields. `github-tickets.json` is the initial work breakdown; `github-ticket-map.json` maps stable planning keys to live issue/item IDs. Live GitHub state is authoritative after creation.

## Workflow

1. Find the ticket covering the work before editing implementation files.
2. Confirm prerequisites and set **In progress** before implementing. Local uncommitted work remains In progress even when local checks pass.
3. Update acceptance criteria and attach concise evidence: behavior, commands executed, failures/limitations, and remaining work.
4. Set **In review** when a non-draft PR exists and link the issue with `Closes #N` where appropriate.
5. Close the issue and use **Done** only after acceptance is verified and changes are merged. Close an epic only after its required sub-issues and phase exit criteria pass.
6. Keep waiting work in Backlog, actionable unstarted work in Ready, and document dependency/blocker links. Do not mark blocked work Done.

Milestone A covers phases 0-5. Milestone B covers phases 6-13. Windows 11 x64 is the current desktop release target; Windows/Linux x64 remain headless targets. ARM64 is a separate deferred ticket and does not block either milestone.

## Commands

```powershell
# Preview or idempotently add missing tickets. Existing initialized statuses are preserved.
./scripts/Sync-GitHubPlan.ps1 -Preview
./scripts/Sync-GitHubPlan.ps1

# Start work and optionally publish a prepared evidence note.
./scripts/Update-ProjectTicket.ps1 -Key P0.4 -Status 'In progress'
./scripts/Update-ProjectTicket.ps1 -Key P0.4 -Status 'In progress' -EvidenceFile artifacts/progress.md

# Review requires an actual open non-draft PR.
./scripts/Update-ProjectTicket.ps1 -Key P0.4 -Status 'In review' -PullRequest 123
```

The bootstrap records IDs after each operation and stops on failures. It reuses mapped issue numbers, including renamed issues, and preserves already-initialized statuses. It never commits, pushes, opens PRs, closes issues, or fabricates completion.

Do not automatically rerun the bootstrap to publish implementation progress: use targeted status/evidence updates. At each work handoff, compare the live board against local implementation status.
