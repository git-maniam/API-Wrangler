# Phase 1 exit review (2026-11-07)

Status: the five specification exit checks pass locally on Windows x64. PR #57 is open with passing hosted checks. P1.1-P1.3 are In review; Phase 1 remains In progress because project editor accessibility ticket #58, review, and merge are open. Accessibility review started on 2026-11-07; automated UI Automation checks pass after a current Release rebuild, while manual keyboard, Narrator, high-contrast, and 150% DPI acceptance remains pending. See [phase-1-accessibility-review.md](phase-1-accessibility-review.md).

| Specification exit check | Evidence |
|---|---|
| Save/load round trip preserves meaning | Project persistence integration tests validate saved asset content and extension fields after reload. |
| Repeated save produces no diff | Canonical repeat-save integration test checks byte-identical output. |
| Invalid files produce useful diagnostics | Tests cover missing required fields with file and JSON pointer; newer schema assets remain inspectable but read-only. |
| Interrupted write retains the prior valid file | Transaction tests cover in-process rollback, interrupted two-file recovery, repeat recovery, and preserving third-party edits on recovery conflict. |
| External edits are detected | Storage tests cover hash conflicts and watcher notifications. Desktop smoke checks clean reload, dirty-draft preservation, stale-save block, explicit conflict choice, recovery of invalid JSON text including immediate shutdown, second-writer read-only state, and future-schema read-only inspection. |

Executed: `dotnet restore ApiWrangler.slnx --locked-mode`, Release solution build (zero warnings/errors), integration tests (26 passed, four optional Node tests skipped), desktop project smoke, and `scripts/Test-Desktop.ps1` HTTP smoke. Repository-wide `dotnet format --verify-no-changes` and `git diff --check` pass after formatting two storage files.

ADR-0002 defines version 1 as the first supported project format. There is no supported pre-v1 migration input to run through a migration preview. The first later schema transition must deliver preview, host-local original-file backups, explicit application, and idempotence. Postman import remains Phase 6.

Project editor accessibility checkpoint: path and asset selectors have access-key labels; Open, Save, Use disk, and Use draft have access keys. The project status is a polite UI Automation live region and raises a change event. The desktop smoke checks accessible names and confirms that both AvalonEdit panes expose their current text through the UI Automation Value pattern, with the disk pane read-only. This is programmatic evidence, not a manual Narrator, keyboard-only, high-contrast, or DPI assessment.

Hosted CI checkpoint: PR #57's first Linux headless run exposed a second-writer lease failure. The lease now combines an in-process owner registry with an OS file lock, and the existing writer-ownership test also checks reacquisition after disposal. After the fix, hosted Windows and Linux headless jobs each passed 30 tests with zero skipped; desktop build, format, and publish passed.

Ticket review: P1.1-P1.3 have local evidence for their current Phase 1 criteria. Migration preview/backup execution is tracked by deferred ticket #60 for the first actual schema transition under ADR-0002. Branch/cache invalidation is already a criterion on #23. Snapshot tests verify captured project bytes and hashed metadata without secret values; Phase 3 ticket #59 tracks execution from an immutable snapshot, including dirty drafts.

Remaining for Phase 1 acceptance: complete manual accessibility review in #58 and merge reviewed changes. The reviewable PR does not make the GitHub tickets Done.

Manual desktop review on Windows 11 x64: use only the keyboard to open a project and select an asset; move between the draft and disk panes; trigger Save, Use disk, and Use draft by access key; and verify focus and status announcements during an external-edit conflict with Narrator. Repeat with Windows high contrast and 150% display scaling, checking that text, buttons, and the status remain visible. Record the results before treating accessibility acceptance as verified.

Parallel review follow-up (2026-11-07): independent review and a single desktop smoke run verified fixes for newer typing during save/recovery cleanup, semantic no-op saves of noncanonical JSON, immutable-ID diagnostics, and rejecting unseen disk revisions before explicit conflict confirmation. One Release desktop build and changed-file format verification passed. Manual accessibility and PR screenshot attachment remain pending.
