# Implementation status

This is a working foundation increment, not completion of the full implementation plan. Neither Milestone A nor Milestone B has been reached.

## Completed behavior

- Pinned .NET 10 solution, central package versions, lockfiles, formatting rules, and Windows/Linux CI definitions.
- Native WPF/MVVM shell with AvalonEdit, HTTP method/URL/header/body editing, response inspection, and cancellation.
- Shared application validation and streaming HTTP engine with bounded previews/artifacts and distinct timeout/cancellation outcomes.
- Read-only, paged 100,000-case grid feasibility fixture.
- CLI `send`, `git-probe`, and `node-probe` commands with meaningful exit codes.
- Installed-Git status/fetch adapter, framed TypeScript/Node worker probe, and worker descendant-cancellation verification.
- Local HTTP fixture, integration tests, architecture checks, desktop smoke/measurement scripts, and architecture decisions.

## Verified on the current Windows x64 host

- Release build: no warnings or errors.
- Seventeen automated tests passed on Windows x64 and Linux x64, including all four explicitly enabled Node-dependent tests; none skipped in those runs.
- Desktop smoke: initial empty-editor two-way binding, HTTP POST round trip, cancellation, and dispatcher ticks during network wait.
- Local bare-remote fetch preserves working files and HEAD. Fetch of this repository's public HTTPS origin also succeeded using inherited Git configuration. This does not verify private-repository authentication or SSH.
- Ten warm process-start samples: p95 834 ms, dataset display p95 154 ms, maximum idle private bytes 87,449,600 (about 83 MiB), 17 realized grid rows, 256 cached dataset rows.
- Node 26.13.0 downloaded to ignored `.tools` and checked against the distributor's SHA-256 manifest; TypeScript compilation and npm audit passed.
- Self-contained Windows x64 preview ZIP rebuilt under locked restore; its published executable launched and passed diagnostics. Normal locked restore still passes after publishing.

Measurement host: Windows x64, AMD Ryzen 7 6800H, eight physical cores, about 21.7 GiB installed memory. Measurements use an empty foundation shell, not an ordinary saved project. No cold-start, index/search, ARM64, accessibility, or full NFR acceptance claim is made. Reproduce with `scripts/Measure-Desktop.ps1`; raw local evidence is in ignored `artifacts/`.

## Phase gates and remaining work

| Phase | Status / next required work |
|---|---|
| 0 | Local foundation implemented; private HTTPS/SSH credential flows, accessibility checks, and broader performance validation remain open; ARM64 is deferred |
| 1 | In progress: project discovery/initialization, schemas, canonical assets, atomic/journaled persistence, watching, recovery, and snapshots are implemented locally; newer schema majors can be inspected but not edited. Nested repository project discovery, forward-version handling, actionable required-field diagnostics, extension preservation, no-diff saves, local settings persistence, dirty-draft preservation through external changes, watcher overflow full-rescan signaling, idempotent interrupted multi-file recovery, preservation on recovery conflict, snapshot metadata, writer ownership, external-edit conflicts, and debounced watcher notification pass. Full phase acceptance and migration/recovery edge cases remain open. |
| 2 | Only basic HTTP feasibility exists; saved workbench, variables, secrets, authentication, cookies, proxy/certificates remain |
| 3 | Not started: collections, assertions, lifecycle, isolated iteration execution, reports, final CLI contracts |
| 4 | Only the read-only grid probe exists; imports, editing, type/null semantics, stable cases, and export remain |
| 5 | Only Git status/fetch probe exists; scoped commit, branching, integration, and conflict resolution remain |
| 6 | Only framed IPC/process probe exists; script execution, trust policy, packages, imports, and compatibility remain |
| 7–9 | Not started: additional protocols, mocks, webhook gateway, local performance engine |
| 10–11 | Not started: control plane, runners, scheduler, distributed load, hosted mocks; do not start before foundation decisions settle |
| 12–13 | Not started: Playwright, visual flows, contract tooling, optional AI, signed installers, operational hardening |

The current desktop release target is Windows 11 x64. ARM64 was explicitly deferred by the user and no longer blocks current milestones. Linux headless build/tests were executed in a local Linux container; hosted GitHub Actions has not executed in this session. Do not equate a workflow definition with an executed hosted check.

Phase 1 checks were verified on Windows x64: nested repository project discovery, newer schema inspection/edit rejection, missing-field diagnostics with file and JSON pointer, extension preservation, no-diff saves, local settings persistence, dirty-draft preservation through external changes, watcher overflow full-rescan signaling, idempotent interrupted multi-file recovery, preservation on recovery conflict, snapshot metadata, second-writer read-only access, external-edit conflict handling, and debounced watcher notification. Latest integration run: 25 passed, four optional Node tests skipped because Node worker variables were not configured. Repository-wide format verification still reports existing formatting violations in storage files outside this slice.

The GitHub project tracks the full plan in phase epics and implementation tickets. Local work remains In progress until reviewed and merged. See `docs/planning/README.md` for synchronization rules.

## Next implementation slice

Finish phase-0 private HTTPS/SSH and accessibility validation. For phase 1, verify migration/backups and actionable schema diagnostics (P1.1), broader transaction recovery (P1.2), and watcher overflow, branch invalidation, and editor conflict handling (P1.3). Then review phase exit criteria before Phase 2. The checkpoint is also recorded in `api-wrangler-prod-spec.md`.
