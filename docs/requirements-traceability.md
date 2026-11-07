# Requirements traceability

| Specification | Current implementation | Evidence / gap |
|---|---|---|
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
