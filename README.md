# API Wrangler

A local-first Windows API testing workbench with a shared headless engine. The Phase 0 foundation is available, Phase 1 core storage is merged into `main`, and Phase 2 HTTP workbench implementation is active. Neither product milestone has been reached. See [implementation status](docs/implementation-status.md) and the [product specification](api-wrangler-prod-spec.md).

## Current status

- Phase 4 dataset import is available for review in [PR #69](https://github.com/git-maniam/API-Wrangler/pull/69): CSV/TSV/JSON/JSONL, explicit mappings and conversions, case IDs, prepared previews and file-backed publication. See [dataset import](docs/dataset-import.md) for usage and limits. Phase 4 remains In progress.

- The WPF desktop shell features a persistent HTTP request workbench, project asset editor with draft recovery and conflict decisions, variable resolution, and a read-only 100,000-case grid. The CLI has `send`, `git-probe`, and `node-probe` commands.
- Phase 1 core storage (version-1 project schemas, discovery, canonical JSON, journaled saves, writer ownership, recovery, watching, local settings, immutable snapshots, and desktop asset editor) is verified and merged into `main` ([PR #57](https://github.com/git-maniam/API-Wrangler/pull/57)). P1.1–P1.3 are **Done**; manual accessibility acceptance remains open in [#58](https://github.com/git-maniam/API-Wrangler/issues/58).
- Phase 2 HTTP workbench is **In progress**:
  - **P2.1 (#12):** Persistent request workbench, repeated headers/query params, multipart/urlencoded/file bodies, compression, cookies, TLS override, and cross-origin redirect auth stripping $\rightarrow$ **Done** ([PR #61](https://github.com/git-maniam/API-Wrangler/pull/61)).
  - **P2.2 (#13):** Variable scopes (hierarchy lookup), bounded nesting, cycle detection, explicit `{{secret:...}}` namespace, Windows DPAPI secret store, deterministic dynamic helpers, and redaction engine $\rightarrow$ **Done** ([PR #62](https://github.com/git-maniam/API-Wrangler/pull/62)).
  - **P2.3 (#14):** Authentication flows and lifecycle $\rightarrow$ **In progress**.

## Overall plan

| Phase | Planned outcome | Current position |
|---|---|---|
| 0 | Shared engine, WPF shell, CLI and process probes, Windows x64 preview | Foundation implemented; private Git authentication, icon packaging, accessibility, and broader acceptance remain |
| 1 | Versioned project format, safe persistence, watching, recovery, snapshots | [PR #57](https://github.com/git-maniam/API-Wrangler/pull/57) in review; [accessibility review #58](https://github.com/git-maniam/API-Wrangler/issues/58) ready |
| 2-5 | Persistent HTTP workbench, variables/secrets/auth, collections/assertions/CLI, datasets, Git collaboration | Phase 2 is **In progress with Gemini**; planned as **Milestone A**, a usable team HTTP regression product |
| 6-9 | Trusted scripting/imports, GraphQL/gRPC/streaming, mocks/webhooks, local performance testing | Planned toward **Milestone B** |
| 10-13 | Runners/control plane, distributed load, Playwright, contracts/docs, installer and release hardening | Complete **Milestone B**, the targeted developer-platform release |

Type-aware editors and font preferences ([#54](https://github.com/git-maniam/API-Wrangler/issues/54)), dataset previews ([#55](https://github.com/git-maniam/API-Wrangler/issues/55)), GraphQL display ([#56](https://github.com/git-maniam/API-Wrangler/issues/56)), and application icon packaging ([#5](https://github.com/git-maniam/API-Wrangler/issues/5)) are planned, not implemented.

The [GitHub project](https://github.com/users/git-maniam/projects/4/views/3) tracks phase epics and tickets; the [tracking workflow](docs/planning/README.md) explains its statuses. The first real schema migration is [deferred until a source and destination version exist](https://github.com/git-maniam/API-Wrangler/issues/60). [Snapshot execution](https://github.com/git-maniam/API-Wrangler/issues/59) belongs to Phase 3, and [branch/cache invalidation](https://github.com/git-maniam/API-Wrangler/issues/23) to Phase 5. Windows 11 x64 is the current desktop target; [ARM64 is deferred](https://github.com/git-maniam/API-Wrangler/issues/53). No delivery dates are assigned.

The next Phase 1 gate is the manual accessibility review in #58. After its findings are resolved, review and merge PR #57, then verify the Phase 1 epic and child tickets before moving any of them to Done.

## Build and verification

Install the .NET SDK pinned in `global.json`. Git is needed for Git integration tests; ordinary HTTP use does not require it. Run only the build or test needed for a changed behavior or required gate, batch related cases, and reuse valid results when inputs have not changed. The commands below are available checks, not a sequence to repeat after every edit.

```powershell
dotnet restore ApiWrangler.slnx --locked-mode
dotnet build ApiWrangler.slnx -c Release --no-restore
dotnet test ApiWrangler.slnx -c Release --no-build
dotnet format ApiWrangler.slnx --verify-no-changes --no-restore
```

On Linux, build/test `tests/ApiWrangler.IntegrationTests` and `src/ApiWrangler.Cli` instead of running the WPF application.

## Run locally

In two terminals:

```powershell
dotnet run --project tests/Fixtures/ApiWrangler.FixtureApi -c Release
dotnet run --project src/ApiWrangler.Desktop -c Release
```

Send the default request, or edit its method, URL, headers, and JSON body. Ctrl+Enter sends; Cancel interrupts an active request. The dataset tab shows a read-only 100,000-case feasibility fixture. The Project assets tab opens an existing version-1 project by directory or manifest path. It edits JSON assets, preserves local drafts, reloads clean files after external edits, and shows both versions when a dirty draft conflicts with disk. Use disk discards the draft; Use draft explicitly allows replacing the disk version on the next save.

```powershell
dotnet run --project src/ApiWrangler.Cli -c Release -- send --url http://127.0.0.1:5057/echo
dotnet run --project src/ApiWrangler.Cli -c Release -- git-probe --repository .
```

`git-probe --fetch` explicitly fetches origin without integrating files. `send` writes raw response bodies locally and prints metadata, not response contents. Redirects are returned without following them. HTTP status codes do not yet run assertions. Project initialization in the desktop, authentication flows, collections, secret management, and the final CLI commands remain unimplemented.

## Optional Node feasibility tests

Install/configure Node 26.13.0, then explicitly build the worker:

```powershell
Push-Location workers/node
npm ci --ignore-scripts
npm run build
Pop-Location
$env:AWR_NODE = (Get-Command node).Source
$env:AWR_NODE_WORKER = Join-Path $PWD 'workers/node/dist/probe.js'
dotnet test tests/ApiWrangler.IntegrationTests -c Release
```

Without these environment variables, the four Node-dependent tests are reported as skipped. The worker only runs a framed IPC/process-cancellation probe; it does not execute user scripts.

## Desktop verification and preview packaging

```powershell
./scripts/Test-Desktop.ps1
./scripts/Measure-Desktop.ps1 -Samples 10
./scripts/Publish-Desktop.ps1 -Runtime win-x64
```

Desktop checks require an interactive Windows desktop. Reports and preview ZIPs are written to ignored `artifacts/`. The current desktop target is Windows 11 x64; ARM64 is deferred. Installers, signing, complete license/SBOM packaging, and public releases remain gated.

To reproduce the Linux headless checks locally:

```powershell
docker build -f scripts/verify-linux.Dockerfile -t api-wrangler-foundation-tests .
docker run --rm api-wrangler-foundation-tests
```
