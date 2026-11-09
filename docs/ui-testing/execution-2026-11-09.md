# Desktop UI execution attempt — 2026-11-09

Environment: Windows 11 Pro x64 10.0.26300, interactive session 1, SDK 10.0.403, source HEAD `156c58e41274a87e1060c7f6b25ae3121e210c4d`. Synthetic project and screenshots are in the disposable local directory recorded by `%TEMP%\awr-current-run.txt`; screenshots have not been uploaded. A clean application source tree was rebuilt in Release; 28 file-copy retry warnings came from the first test app instance holding DLLs. The build completed with zero errors after that instance was closed.

The first exploratory run used an older desktop binary. Findings were repeated after rebuilding current source: UI Automation selected Project assets, HTTP workbench and Import dataset, but enumerated 34 window descendants and could not find any selected-tab controls. See [#157](https://github.com/git-maniam/API-Wrangler/issues/157). The current-build screenshot of Project assets shows the project path and asset labels overlapping; see [#158](https://github.com/git-maniam/API-Wrangler/issues/158).

- UI-28 and UI-61: partial failure on accessibility/legibility; remaining variants not executed. No Narrator speech observation was performed.
- UI-01: blocked for this unattended run by #157; the reported Open project issue [#95](https://github.com/git-maniam/API-Wrangler/issues/95) is not independently reproduced. Other cases remain unexecuted; do not interpret diagnostics below as case passes.
- Fresh-build `scripts/Test-Desktop.ps1` passed: editor round trip, HTTP round trip, cancellation and tab layout flags true; dispatcher ticks 9. It uses programmatic view-model paths and does not verify button interaction or UI Automation tree.
- Before the rebuild, project diagnostics passed draft, conflict, clean reload, invalid-text recovery, shutdown recovery, second-writer and future-schema checks. Dataset diagnostics passed two-row conversion, stale options and prepared source preservation. Git diagnostics passed its clone, branch, push, reviewed commit, history and scoped restore checks. These results apply to the older binary and are not current-build acceptance evidence.

Next: resolve or work around #157 with a reliable interactive automation mechanism, then execute all 61 cases and record each positive/negative outcome independently. Reproduce #95 through the actual UI. Re-run current-build project/dataset/Git diagnostics only when useful to the relevant case; do not report old-binary results as current. TLS cases need OpenSSL; `openssl` was not found on PATH in this session. Fixtures and test data remain local and uncommitted.

## Continued execution — 23:01 IST

An isolated in-process WPF harness exercised actual controls and button automation peers against the current Release build and local fixtures. This bypasses the external UI Automation tree defect; it does not establish physical mouse, keyboard, or Narrator behavior. Its source is under ignored `artifacts/ui-execution-harness/`, and JSON observations are in the disposable run directory named by `%TEMP%\awr-current-run.txt`. The current harness source contains the protocol checks; earlier JSON files retain the other observations.

- UI-01: the actual Open project button peer opened a disposable directory and manifest and displayed a diagnostic for a nonexistent path. The reported physical-click issue #95 is still unconfirmed.
- UI-05: POST echoed repeated headers/query and synthetic body; malformed header showed a diagnostic. Remaining case checks are recorded in #100.
- UI-06: valid JSON formatted and invalid JSON produced a parse error. A missing file path sent an empty request with HTTP 200: [#160](https://github.com/git-maniam/API-Wrangler/issues/160).
- UI-10, UI-11, UI-38 and UI-39: dataset preparation/import, duplicate or missing IDs, conversion preview and invalid delimiter produced the observations recorded on #106, #107, #132 and #135. Persistence comparisons and remaining variants are still open.
- UI-43: automatic detection previewed Postman collection/environment, OpenAPI, HAR and cURL fixtures. Publishing and explicit-format variants remain open; malformed-input and cancellation attempts were inconclusive because of harness timing/state and are not reported as product failures.
- UI-44: source changed after preview still allowed publication of the old request: [#159](https://github.com/git-maniam/API-Wrangler/issues/159).
- UI-45: valid GraphQL query succeeded; malformed variables were silently ignored and the query still succeeded: [#161](https://github.com/git-maniam/API-Wrangler/issues/161).
- UI-47: valid proto reported one service/method; arbitrary non-proto text was reported as successfully parsed: [#162](https://github.com/git-maniam/API-Wrangler/issues/162).

All findings are partial case results; no case has been marked Done. Continue with remaining variants across all tabs, correcting harness state/timing before interpreting ambiguous results. Restart local fixtures as documented; the processes started for this run were stopped at handoff.

**Session closed at user request, 23:04 IST.** Project 4 issues #157–#162 remain Ready; #95 remains Backlog pending physical-click reproduction. Execution closure was noted on #95, #96, #139 and #157. No further execution is active. Resume the incomplete variants only when requested.
