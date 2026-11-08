# Requirements traceability

| Specification | Current implementation | Evidence / gap |
|---|---|---|
| 7.2, 10.1-10.3 | File-backed import, stable case IDs, streaming conversions, encoding selection and desktop prepare/accept | 62 dataset/persistence tests plus desktop dataset smoke pass. Immutable source publication and project snapshots verified. Flattening, friendlier mapping UI and manual accessibility remain; see dataset-import.md. |
| 10.3, 27 | JSON-array record admission bound before deserialization | 46 dataset/rollback tests pass; oversized source is rejected within 4 KiB read-ahead, exact UTF-8 limits and 1,000 nested/escaped records verified. File-backed import publication remains pending. |
| 7.2, 10.1, Phase 4 import integrity | Prepared inline dataset imports, preserved case IDs, explicit missing-ID addition, duplicate rejection and journaled commit | 43 dataset/rollback tests pass; stale writes, late parse failure, cancellation and snapshot bytes verified. Large file-backed import and UI integration remain in #20. |
| 10.2-10.3 | Typed streaming JSON-array/JSONL readers and bounded headless mapping/conversion preview | 31 combined dataset tests pass, including nested JSON, missing/null, duplicate properties, explicit culture/null conversions, cancellation and preview limits. Transactional import, stable case IDs, array record admission bounds and UI remain in #20. |
| 10.2 | Streaming raw delimited dataset reader with explicit delimiter and bounded records | 11 focused tests pass for multiline/escaped values, leading zeros, BOM, malformed headers/rows, buffer boundaries, bounds, and cancellation. Preview/conversion, encoding UI, and transactional import remain in #20. |
| 3, 32 phase 0 | Domain → Application ← Execution, WPF and CLI hosts | FoundationTests architecture boundary test; Windows 11 x64 current scope, ARM64 deferred |
| 3.2, 26 | Cancellable MVVM shell, native editor, labels and keyboard send | Desktop smoke verifies binding/network responsiveness and project-editor UI Automation values; manual Windows 11 accessibility review is tracked in #58 |
| 4-5, 14.2, 30 | Version-1 schemas, canonical project assets, journaled saves, watching, recovery, immutable saved-file snapshots, and desktop draft/conflict editor | PR #57 hosted Windows/Linux headless jobs each passed 30 tests with zero skipped; P1.1-P1.3 In review, manual editor accessibility #58 In progress; future migration #60, cache invalidation #23, snapshot execution #59 |
| 8.3 | TLS defaults, explicit no-follow redirects, no application retries | Redirect/auth-forwarding test; advanced transport policies pending |
| 8.5, 27.3 | Streamed response files, 5 MiB preview / 100 MiB saved-body defaults | Large-response and response-limit cleanup tests |
| 11.6 | Explicit result outcomes; timeout distinct from cancellation | HTTP deadline/cancellation tests; assertion/aggregate semantics pending |
| 6.1, 6.8 | Installed Git, argument arrays, separate fetch | Bare remote and public-origin fetch; private credentials/SSH pending |
| 12.3 | Node IPC framing, version/correlation, deadlines, process-tree cancellation | NodeTests; production script trust and job-object containment pending |
| 10.4, 27 | Bounded page cache and virtualized read-only grid fixture | Random-access cache test; desktop realization measurements |
| 28 | Additional foundation `send`/probe commands | Final project/collection CLI commands are not implemented |
| 29, 31 | Cross-platform CI definition and local fixture suite | PR #57 hosted Windows/Linux headless jobs each passed 30 tests with zero skipped; desktop build, format, and publish passed |
| 7, 9-25 | Remaining production capabilities | See implementation-status and the specification; no completion claimed |

Traceability distinguishes experiments from accepted production features. A command or button does not establish feature completion.
