# Phase 1 exit review (2026-11-07)

Status: the five specification exit checks pass locally on Windows x64. PR #57 is open for review. Phase 1 remains In progress because manual Narrator/keyboard/high-contrast review, ticket acceptance gaps, and merge are still open.

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

Ticket review: P1.1's three criteria have local evidence. P1.2's write/recovery and writer-ownership criteria have local evidence; migration preview/backup execution remains scoped to the first actual schema transition by ADR-0002. P1.3's clean reload, dirty conflict, and snapshot criteria have local evidence. Watcher overflow full-rescan signaling is verified; branch/cache invalidation waits for a project cache. Snapshot tests verify captured project bytes and hashed metadata without secret values; run execution does not yet consume snapshots.

Remaining for Phase 1 acceptance: complete manual accessibility review, reconcile the scoped P1.2/P1.3 criteria with ticket acceptance, and merge reviewed changes. The reviewable PR does not make the GitHub tickets Done.

Manual desktop review on Windows 11 x64: use only the keyboard to open a project and select an asset; move between the draft and disk panes; trigger Save, Use disk, and Use draft by access key; and verify focus and status announcements during an external-edit conflict with Narrator. Repeat with Windows high contrast and 150% display scaling, checking that text, buttons, and the status remain visible. Record the results before treating accessibility acceptance as verified.
