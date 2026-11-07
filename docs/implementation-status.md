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

## Historical Phase 0 verification on Windows x64

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
| 1 | Merged (PR #57): project discovery, versioned schemas, canonical assets, journaled persistence, watching, recovery, snapshots, and a desktop asset editor are implemented and merged into main. Storage checks pass; desktop smoke covers clean reload, dirty-draft conflict decisions, invalid-text draft recovery, and UI Automation values. P1.1 (#8), P1.2 (#9), and P1.3 (#10) are Done. Manual accessibility review (#58) remains pending. |
| 2 | In progress: P2.1 and P2.2 implemented on `phase2-variables-and-secrets`. Request workbench with full lifecycle controls, repeated headers/query params, multipart/urlencoded/file-backed bodies, automatic decompression, cookies, explicit TLS override, cross-origin redirect auth stripping, and non-idempotent retry protection. P2.2 delivered variable resolution with documented precedence (RequestLocal > Dataset > RunOverride > LocalEnvironment > Environment > Collection > ProjectDefault), bounded nesting ($\le 10$), circular reference detection with cycle trace, non-shadowed explicit `{{secret:...}}` namespace, Windows DPAPI secret store (`LocalSecretStore`), deterministic dynamic helpers, and redaction engine across headers, logs, and execution previews. Release build, 44 integration tests, and desktop smoke pass. |
| 3 | Not started: collections, assertions, lifecycle, isolated iteration execution, reports, final CLI contracts |
| 4 | Only the read-only grid probe exists; imports, editing, type/null semantics, stable cases, and export remain |
| 5 | Only Git status/fetch probe exists; scoped commit, branching, integration, and conflict resolution remain |
| 6 | Only framed IPC/process probe exists; script execution, trust policy, packages, imports, and compatibility remain |
| 7–9 | Not started: additional protocols, mocks, webhook gateway, local performance engine |
| 10–11 | Not started: control plane, runners, scheduler, distributed load, hosted mocks; do not start before foundation decisions settle |
| 12–13 | Not started: Playwright, visual flows, contract tooling, optional AI, signed installers, operational hardening |

The current desktop release target is Windows 11 x64. ARM64 was explicitly deferred by the user and no longer blocks current milestones. PR #57's hosted GitHub Actions passed after the Linux writer-lease fix: Windows and Linux headless jobs each passed 30 tests with zero skipped, and desktop build, format, and publish passed. The P1.4 accessibility review has started: a current Release desktop build and project smoke pass, including UI Automation names, live-region metadata, and draft/disk Value patterns. Manual keyboard-focus, Narrator, high-contrast, and 150% DPI checks remain pending because this session has no interactive desktop. See `docs/phase-1-accessibility-review.md`.

Phase 1 checks were verified on Windows x64: nested repository project discovery, newer schema inspection/edit rejection, missing-field diagnostics with file and JSON pointer, extension preservation, no-diff saves, local settings persistence, dirty-draft preservation through external changes, watcher overflow full-rescan signaling, in-process transaction rollback, idempotent interrupted multi-file recovery, preservation on recovery conflict, snapshot metadata, second-writer read-only access, external-edit conflict handling, and debounced watcher notification. Desktop project smoke verifies preserved dirty JSON draft, blocked stale save, explicit use-draft decision, automatic clean-editor reload, invalid-text recovery, and read-only second-writer/newer-schema inspection. Desktop HTTP smoke verifies binding, send, cancellation, and dispatcher responsiveness. Latest full Release build passed with zero warnings/errors; integration run: 26 passed, four optional Node tests skipped because Node worker variables were not configured. Repository-wide format verification passes.

The GitHub project tracks the full plan in phase epics and implementation tickets. PR #57 contains the Phase 1 continuation; P1.1-P1.3 are In review, P1.4 accessibility review is In progress, and the phase epic remains In progress. See `docs/planning/README.md` for synchronization rules.

## Next implementation slice

Phase 1 continuation (2026-11-07): ADR-0002 defines version 1 as the first supported format; no pre-v1 migration input exists. The first version transition's preview/backups/idempotence are tracked in deferred ticket #60. The desktop edits project assets, compares disk content, reloads clean editors, and requires an explicit Use disk/Use draft conflict decision. Project and HTTP desktop smoke checks pass, including invalid-text recovery and second-writer read-only behavior. The five specification exit checks pass locally; see `docs/phase-1-exit-review.md`. The editor has access keys, a live status region, and programmatically checked UI Automation names and values. Manual Narrator, keyboard-only, high-contrast, and DPI review are tracked in In progress ticket #58. Branch/cache invalidation is tracked in #23, and execution from immutable snapshots in #59. PR #57 is open; P1.1-P1.3 are In review while Phase 1 remains In progress.

## Parallel review checkpoint (2026-11-07 20:37 IST)

Two agents reviewed code and acceptance evidence, with independent review of the fixes. The editor now preserves newer typing and recovery through save/cleanup awaits, reports immutable-ID errors, and requires another comparison when disk content changes before Use draft. Conflicting commands are blocked during operations and watcher refreshes are deferred.

Validation: one Release desktop build passed with zero warnings/errors; one --project-smoke run passed existing workflows and deterministic save/cleanup, noncanonical no-op, immutable-ID, and unseen-disk-revision regressions. Changed-file format verification passed. Local evidence and a synthetic editor screenshot are in ignored artifacts/phase1-review-smoke/run-20261107-203638. The screenshot is not manual accessibility evidence; PR screenshot attachment remains pending. Hosted checks for pre-fix commit 18dc2b6 passed; the pushed fix requires its own CI result. P1.3 returns to In review with PR #57; #58 and Phase 1 remain In progress. Manual accessibility is deferred at the user's request.

## Phase 2 Progress (2026-11-07 21:45 IST)

- **P2.1 (Issue #12, PR #61, commit `2559162`):** Request workbench, repeated query and header parameter preservation/ordering, multipart/urlencoded/file-backed streaming bodies, decompression, cookies, explicit TLS override, cross-origin redirect auth stripping, non-idempotent retry protection. Verified and merged into `main`.
- **P2.2 (Issue #13, PR #62, commit `62e6ea6`):** Variable resolution precedence, bounded nesting, circular reference detection, explicit non-shadowed `{{secret:...}}` namespace, Windows DPAPI `LocalSecretStore`, deterministic dynamic helpers, and `RedactionEngine`. Verified and merged into `main`.
- **P2.3 (Issue #14):** Authentication flows and lifecycle: Basic, Bearer, API key (header & query), OAuth 2.0 (client credentials, refresh token, PKCE authorization code with `OAuthPkceHelper`), Windows integrated authentication, Client certificates (mTLS via `X509CertificateLoader`), `RedactionEngine` credential stripping from previews and logs, and early rejection of unsupported authentication types (`UnsupportedAuthException`). 55 tests passed (0 failed, 4 skipped Node tests); Release desktop smoke passed; format verified. Ready for PR and merge into `main`.
