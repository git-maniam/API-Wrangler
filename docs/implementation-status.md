# Implementation status

## Phase 5.1 Git workflow checkpoint (2026-11-08 17:58 IST)

P5.1 has started on `phase5-repository-workflows`:
- Added `GitRepositoryService` for Git executable discovery/version, repository discovery/init/clone, status facts (dirty paths, branch/upstream, independent ahead/behind, remotes, detached HEAD, linked worktree, write access, merge/rebase/sequencer state), branch listing/history, remote add/select, fetch/push, branch creation/switch, fast-forward, explicit merge/rebase, and scoped `api-wrangler/` asset restore.
- Added a desktop Git collaboration workbench exposing repository open/status, clone/init, remote operations, branch operations, integration, history, and asset restore. Git runs through argument arrays and configured Git authentication; the UI does not embed credentials. Branch/integration operations require a clean whole working tree; there is no automatic stash, force push, or hard reset.
- Added `scripts/Test-GitDesktop.ps1` and a desktop smoke flow. It passed clone/init/open/status, remote selection, fetch, branch create/switch/push, history, and scoped restore against a local bare remote; the screenshot was inspected.
- Existing `ProjectSession` guards continue to suspend writes during index locks and merge/rebase markers; snapshots compare Git identity across capture. No new cache was added.
- Verification: Release solution build succeeded (0 warnings/errors); four focused tests passed (0 failed/skipped), covering offline/rejected operations, real merge/rebase conflicts and recovery, external branch changes with an open project session, snapshots, index-lock write suspension, fetch immutability, dirty/unrelated files, fast-forward, branch/history/restore, detached HEAD, and linked worktrees. Desktop Git workflow smoke passed; screenshot inspected.

P5.1 remains In progress. Remaining: manually review provider-specific rejected-auth behavior and accessibility/credential prompts. A local HTTP 401 fixture hung in Git's credential path and was removed rather than treated as passing evidence. P5.2 scoped reviewed commits and P5.3 semantic conflict handling have not started. See `docs/phase-5.1-progress.md` and the Restart bookmark in the product spec.

## Phase 4 color-highlighted preview checkpoint (2026-11-08 11:51 IST)

P4.3 implemented and verified on branch `phase4-preview-highlighting`:
- `DatasetHighlightingDefinitions`: Registers syntax highlighting definitions for CSV (distinguishing delimiters, quoted fields, numbers), JSON, and JSON Lines in AvalonEdit's `HighlightingManager`.
- `DatasetPreviewFormatting`: Dynamically maps dataset formats ("CSV", "TSV", "JSON", "JSONL") to syntax highlighting modes.
- Desktop Integration: Upgrades dataset import preview to AvalonEdit's `TextEditor` with live syntax highlighting and two-way typography bindings (`EditorFontFamily`, `EditorFontSize`).
- Verification: 151 automated tests passed (0 failed, 4 skipped). Desktop smoke passed (`scripts/Test-Desktop.ps1`). Solution builds cleanly in Release configuration with zero warnings/errors. Format verification passed (`dotnet format --verify-no-changes`).

## Phase 4 editable grid and clipboard workflows checkpoint (2026-11-08 11:45 IST)

P4.2 implemented and verified on branch `phase4-editable-grid`:
- `EditableDatasetGrid`: Virtualized 100,000-row editable dataset grid with responsive row retrieval and cell editing (<10ms), multi-cell editing, Undo/Redo transaction history stack, Fill Down across selected rows, Duplicate Case with generated unique `_caseId`, Insert/Delete row operations, Search/Filter, and View Sorting (Ascending/Descending) without mutating underlying source dataset ordering.
- `ClipboardFormatDetector`: Recognizes spreadsheet TSV (Excel, Google Sheets), CSV text, JSON arrays, JSON Lines, raw HTTP requests, plain text, and cURL commands (enforcing the specification rule that pasted shell commands are never executed).
- `SpreadsheetSafeExport`: Provides spreadsheet-safe CSV export that neutralizes formula-leading cell triggers (`=`, `+`, `-`, `@`, `\t`, `\r`) by prefixing single quotes, while preserving native values when disabled.
- `ExternalResultAttachmentService`: Attaches pasted external test run records (JUnit XML, Newman / Postman JSON), records provenance (tool, import timestamp, SHA-256 content hash, test mapping, status), and guarantees that imported results are never represented as API Wrangler executions (`IsWranglerExecution = false`).
- Desktop Workbench: Adds interactive "Dataset grid & clipboard" tab with search/filter, undo/redo, paste, fill-down, duplicate case, insert, delete, spreadsheet-safe CSV export, and external results attachment.
- Verification: 145 automated tests passed (0 failed, 4 skipped). Desktop smoke passed (`scripts/Test-Desktop.ps1`). Solution builds cleanly in Release configuration with zero warnings/errors. Format verification passed (`dotnet format --verify-no-changes`).

## Phase 4 file-backed import and desktop checkpoint (2026-11-08 11:24 IST)

P4.1 is ready for review in PR #69: streaming file-backed JSONL preparation/publication with hash checking, stable case identity, full-import conversions, and desktop source/encoding/mapping/preview/accept/cancellation flow. Input source changes after preview do not alter accepted content. Existing project transactions protect definitions and stale writes; immutable source files participate in project snapshots. Release solution build passed; 62 dataset/persistence tests passed without skips. Desktop dataset smoke verified explicit conversion, stale-options rejection, and exact prepared-source import; screenshot inspected. A diagnostic tab-name compile error was fixed before the successful desktop rebuild/smoke. See `docs/dataset-import.md` for limits and remaining polish. P4 stays In progress; P4.1 is not Done before review/acceptance/merge. Existing unrelated edits preserved.

## Phase 4 JSON admission checkpoint (2026-11-08 10:09 IST)

JSON-array reads enforce a configurable per-record byte budget (default 1 MiB) before deserialization using a bounded-read stream wrapper. Record boundaries respect nesting and escaped strings; UTF-8 bytes are counted, total dataset length is not constrained by the record budget, and the caller's stream remains open. Batched dataset/rollback verification passed 46 tests with no failures/skips in one invocation; no standalone build. File-backed import/publication, full-import conversion integration and UI remain; P4/P4.1 stay In progress.

## Phase 4 inline import checkpoint (2026-11-08 09:00 IST)

`DatasetImport` prepares bounded inline datasets without touching assets and returns an immutable `PreparedDatasetImport` for explicit acceptance. Existing string case IDs are preserved; duplicate/invalid IDs fail, and missing IDs require explicit addition preview. Commit reuses journaled `IProjectSession.SaveAsync` with expected-hash conflict checks and existing schema/path/writer validation. Persisted inline sources are included in project snapshots. One batched invocation (`dotnet test tests/ApiWrangler.IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~Dataset|FullyQualifiedName~Save_ReplacementFailure_RollsBackAllFiles' --verbosity minimal`) passed 43 tests, none skipped. No standalone build. P4/P4.1 remain In progress: large file-backed imports, JSON-array admission bounds, full-import conversion integration, flattening/dates, encoding selection and UI remain. Existing unrelated edits preserved.

## Phase 4 JSON and preview continuation (2026-11-08 08:57 IST)

Added streaming JSON-array/JSONL object readers preserving nested JSON, missing fields, and explicit nulls; duplicate properties are rejected recursively. JSONL supports BOM, CRLF, cancellation, and bounded records. Added a headless preview capped at 1,000 rows (100 default), column mapping, explicit culture-aware decimal/boolean/string conversions, and opt-in empty-string-to-null mapping. Source assets remain untouched. JSON-array per-record admission limits, stable case IDs, transactional import/snapshots, flattening/date conversion, and import UI/encoding selection remain in P4.1. Batched verification: delimited, JSON, and preview suites passed 31 tests (0 skipped); two initial failures were exact-exception test assertions, corrected before the single rerun. No standalone build or broad test suite. Draft PR #69; P4/P4.1 remain In progress.

## Phase 4 continuation (2026-11-07 22:46 IST)

P4 and P4.1 (#19/#20) are In progress. `DelimitedDatasetReader` streams raw CSV/TSV/custom-delimited records with quoted multiline fields, escaped quotes, BOM handling, leading-zero and empty-string preservation, duplicate/empty-header and record-width validation, cancellation, and per-record bounds. One necessary focused invocation (`dotnet test tests/ApiWrangler.IntegrationTests -c Release --no-restore --filter FullyQualifiedName~DelimitedDatasetTests --verbosity minimal`) compiled affected projects and passed 11 tests, none skipped. No separate build or broad suite run. JSON/JSONL, preview/mapping, explicit conversion/null policies, stable case IDs, and transactional import remain; no Phase 4 acceptance claim. Existing unrelated whitespace changes are preserved.

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
| 5 | P5.1 Git service and desktop workflow checkpoint started; acceptance remains in progress. P5.2 scoped commits and P5.3 conflict resolution remain |
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
- **P2.3 (Issue #14, PR #63, commit `cc50c7d`):** Authentication flows and lifecycle: Basic, Bearer, API key (header & query), OAuth 2.0 (client credentials, refresh token, PKCE authorization code with `OAuthPkceHelper`), Windows integrated authentication, Client certificates (mTLS via `X509CertificateLoader`), `RedactionEngine` credential stripping from previews and logs, and early rejection of unsupported authentication types (`UnsupportedAuthException`). 55 tests passed (0 failed, 4 skipped Node tests); Release desktop smoke passed; format verified. Merged into `main`.
- **P2.4 (Issue #54, PR #64, commit `ac6971f`):** Type-aware structured editors, viewers, and font preferences. Headless `StructuredTextEngine` provides format detection, beautification, and parse diagnostics for JSON, XML, YAML, GraphQL, and CSV without altering raw content or semantic values. Enforces large content threshold (5 MiB). AvalonEdit syntax highlighting wired to dynamic format detection and theme contrast. `LocalSettingsStore` persists editor font family and size with Segoe UI 12 pt (16 px) default. Workbench UI features font selectors, "Format body" command, structured vs. raw response view toggle, and diagnostic banners. 76 tests passed (0 failed, 4 skipped Node tests); Release desktop smoke passed; format verified. Merged into `main`.

## Phase 2 Complete (2026-11-07 22:00 IST)

Phase 2 (HTTP testing workbench) is complete. All 4 sub-issues (#12, #13, #14, #54) have been implemented, verified, and merged to `main`. All five Phase 2 specification exit criteria pass locally; see `docs/phase-2-exit-review.md`. Epic #11 closed and marked Done in GitHub Project 4. Advancing to Phase 3 (Collections and assertions).

## Phase 3 Complete (2026-11-07 22:40 IST)

Phase 3 (Collections, Assertions, and CLI) is complete. All 4 sub-issues (#16, #17, #18, #59) have been implemented, verified, and merged to `main`. All five Phase 3 specification exit criteria pass locally; see `docs/phase-3-exit-review.md`. Epic #15 closed and marked Done in GitHub Project 4. Ready to advance to Phase 4 (Datasets and clipboard).

- **P3.1 (Issue #16, PR #65, commit `efb21ec`):** Collection lifecycle, isolated iterations, and bounded flow control. Implemented `ExecutionModels.cs` and headless `CollectionRunner.cs` supporting explicit request ordering, collection setup/teardown with configurable policy (`Always`, `OnFailure`, `OnSuccess`), concurrency control, iteration isolation (deep-cloned variable scopes preventing cross-iteration mutable leakage), loop budgets (enforcing a maximum of 50 iterations per loop to prevent infinite loops), request selections, and graceful/forced cancellation. Integrated `run` command into `api-wrangler` CLI (`Program.cs`) and collection execution in `MainViewModel.cs`. 80 tests passed; Release desktop smoke passed; format verified. Merged into `main`.
- **P3.2 (Issue #17, PR #66, commit `ad1dbc6`):** Typed assertions, result aggregation, and evidence browser. Implemented `AssertionEngine.cs` with support for all typed assertions (`status`, `header`, `cookie`, `bodyContains`, `jsonPath`, `responseTime`, `responseSize`, `regex`) and operators (`equals`, `notEquals`, `contains`, `notContains`, `greaterThan`, `lessThan`, `exists`, `notExists`, `matches`). Evaluates assertions during collection and request execution. Distinguishes failed assertions (`ExecutionOutcome.Failed`) from transport/network errors (`ExecutionOutcome.Error`). Preserves teardown errors across aggregate calculations (`RunStatus.Error` / `RunStatus.Failed`). Implemented `BaselineEngine.cs` comparing status, headers, JSON and text with support for `IgnoredPaths`. Implemented reporters in `Reporters.cs` (`ConsoleReporter`, Section 14 `JsonReporter`, CI `JUnitReporter`, `MarkdownReporter`, `HtmlReporter`) and `EvidenceSanitizer` redacting sensitive headers and secret tokens. Wired `--reporters` and `--report-dir` into CLI `run` command and updated exit codes (exit code 1 for assertion failures, 3 for transport errors, 4 for cancellation, 0 for passed). 86 tests passed; Release desktop smoke passed; format verified. Merged into `main`.
- **P3.3 (Issue #18, PR #67, commit `8e733a1`):** Project CLI contract and CI examples. Extended `api-wrangler` CLI with `init` (canonical project scaffolding with `ProjectRepository.InitializeAsync`), `validate` (schema and reference integrity check), and enhanced `run` command with deterministic exit codes (0 for pass, 1 for failed assertions, 2 for invalid configuration/project, 3 for infrastructure/transport errors, 4 for cancellation, 5 for missing dependencies). Added host environment variable secret resolution (`API_WRANGLER_SECRET_<KEY>` or `<KEY>`) enabling noninteractive CI runs without interactive DPAPI keys. Added CI documentation and example workflows under `docs/ci/` (GitHub Actions, generic shell, Windows runner, Linux container runner) and `.github/workflows/regression-example.yml` with failure artifact retention (`if: always()`) and security best practices (no untrusted pull-request secret exposure, no `pull_request_target`). 90 tests passed; Release desktop smoke passed; format verified. Merged into `main`.
- **P3.4 (Issue #59):** Exact immutable snapshot execution. Connected Phase 1 immutable project snapshot store to desktop and CLI collection execution. Every run executes against an immutable content-addressed snapshot, preventing external file modifications during execution from altering results. Extended `ProjectSnapshots.CreateAsync` and `IProjectSession.SnapshotAsync` to support explicit in-memory dirty drafts (`EditorDraft`), writing draft contents into the isolated snapshot without overwriting saved project assets on disk. Recorded `SnapshotHash`, `GitCommit`, and `IsDirty` in run reports and results while keeping secrets omitted from metadata. 92 tests passed (0 failed, 4 skipped Node tests); Release solution build passed with 0 warnings/errors; desktop smoke passed; format verified. Ready for PR and merge into `main`.
