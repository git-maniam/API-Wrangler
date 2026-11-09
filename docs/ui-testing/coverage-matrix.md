# Desktop control coverage map

This maps the current `MainWindow.xaml` surface to cases. Every listed case has a positive and a negative variant; scenario groups may contain multiple actions. This is planned coverage, not evidence of execution. UI-01 through UI-61 map to project issues in `cases.json`.

| Surface and controls | Cases |
| --- | --- |
| Shell: tabs, font/size, focus, keyboard, shortcut, resizing, splitters, high contrast | UI-28, UI-61 |
| Project assets: path, Open, selector, editor, disk comparison, Save, Use disk/draft, dirty switch, watcher, read-only ownership, recovery | UI-01, UI-02, UI-03, UI-29, UI-30 |
| HTTP: New, Save, Close, saved request, environment, name, methods, URL, Send/Cancel, shortcut | UI-04, UI-05, UI-07, UI-28, UI-31 |
| HTTP nested input tabs: repeated headers/query, all body modes/file/format, redirects/TLS, variables, all auth selector values | UI-05, UI-06, UI-08, UI-09, UI-32, UI-33, UI-34, UI-36 |
| HTTP outputs: body Pretty/Raw and diagnostics, headers, cookies, artifact, large response | UI-07, UI-35 |
| Import dataset: Browse, name, four formats, three encodings, delimiter, culture, mappings/types/nulls, IDs, preview, accept and both cancellation controls | UI-10, UI-11, UI-12, UI-37, UI-38, UI-39, UI-40, UI-41 |
| Import & export: Browse, source text, all six format selections, preview, summary, warnings, publish | UI-13, UI-42, UI-43, UI-44 |
| Protocols: GraphQL endpoint/query/variables/format/execute/result; gRPC endpoint/method/proto/payload/parse/simulate/transcript; WebSocket/SSE endpoint/connect/send/disconnect/transcript | UI-14, UI-15, UI-16, UI-45, UI-46, UI-47, UI-48 |
| Mocks & Webhooks: host/port/external opt-in, start/stop, all rule fields and fault options, rule list, logs/refresh/reset, listener prefix/start/stop/events/refresh | UI-17, UI-18, UI-19, UI-49, UI-50, UI-51 |
| Load testing: URL, method, VUs, duration, both models, thresholds, confirmation, start/cancel/report | UI-20, UI-52 |
| Dataset feasibility: virtualized read-only rows and scrolling | UI-21 |
| Dataset grid: filter/clear, edit, undo/redo, paste CSV/TSV/JSON/cURL, duplicate, fill, insert/delete, safe export, attach external results | UI-22, UI-23, UI-53, UI-54, UI-55 |
| Git: executable/path, initialize/clone/refresh, remotes/add/select/fetch/push, branch/create/switch/fast-forward/history, compare/merge/rebase, restore | UI-24, UI-27, UI-56, UI-57, UI-58, UI-60 |
| Git commit review: selected paths, message, review diff and commit | UI-25 |
| Git conflicts: find/load, base/local/incoming, editable result, each review choice including delete/save both, final diff, resolve, recovery | UI-26, UI-59 |

The UI has no collection-run controls despite a view-model command; CLI/backend workflows and unavailable UI features are outside this matrix. Accessibility speech observation remains #58. A mapped control is not proof that its observed behavior matches the expected contract.
