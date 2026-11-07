# API Wrangler

A local-first Windows API testing workbench with a shared headless engine. The Phase 0 foundation is available, and the Phase 1 project-format and persistence work is in review. Neither product milestone has been reached. See [implementation status](docs/implementation-status.md) and the [product specification](api-wrangler-prod-spec.md).

## Current status

- The WPF desktop shell can edit and send basic HTTP requests, inspect bounded responses, cancel an active request, and display a read-only 100,000-case grid. The CLI has `send`, `git-probe`, and `node-probe` feasibility commands.
- Version-1 project schemas, discovery, canonical JSON, journaled saves, writer ownership, recovery, watching, local settings, immutable saved-file snapshots, and a desktop JSON asset editor are implemented in [PR #57](https://github.com/git-maniam/API-Wrangler/pull/57). The editor preserves drafts, reloads clean files, and requires a choice when a draft conflicts with disk.
- The five Phase 1 specification exit checks pass locally. After the Linux writer-lease fix, hosted Windows and Linux headless jobs each passed 30 tests with zero skipped; desktop build, format, and publish passed. [Phase 1 exit review](docs/phase-1-exit-review.md) records the evidence and limits.
- P1.1-P1.3 are **In review**. The Phase 1 epic remains **In progress**: manual Windows 11 keyboard, Narrator, high-contrast, and DPI review is [Ready in #58](https://github.com/git-maniam/API-Wrangler/issues/58). Phase 0 private HTTPS/SSH acceptance is also still open. No Phase 1 ticket is Done before review, acceptance, and merge.

## Overall plan

| Phase | Planned outcome | Current position |
|---|---|---|
| 0 | Shared engine, WPF shell, CLI and process probes, Windows x64 preview | Foundation implemented; private Git authentication, icon packaging, accessibility, and broader acceptance remain |
| 1 | Versioned project format, safe persistence, watching, recovery, snapshots | [PR #57](https://github.com/git-maniam/API-Wrangler/pull/57) in review; [accessibility review #58](https://github.com/git-maniam/API-Wrangler/issues/58) ready |
| 2-5 | Persistent HTTP workbench, variables/secrets/auth, collections/assertions/CLI, datasets, Git collaboration | Planned as **Milestone A**, a usable team HTTP regression product |
| 6-9 | Trusted scripting/imports, GraphQL/gRPC/streaming, mocks/webhooks, local performance testing | Planned toward **Milestone B** |
| 10-13 | Runners/control plane, distributed load, Playwright, contracts/docs, installer and release hardening | Complete **Milestone B**, the targeted developer-platform release |

Type-aware editors and font preferences ([#54](https://github.com/git-maniam/API-Wrangler/issues/54)), dataset previews ([#55](https://github.com/git-maniam/API-Wrangler/issues/55)), GraphQL display ([#56](https://github.com/git-maniam/API-Wrangler/issues/56)), and application icon packaging ([#5](https://github.com/git-maniam/API-Wrangler/issues/5)) are planned, not implemented.

The [GitHub project](https://github.com/users/git-maniam/projects/4/views/3) tracks phase epics and tickets; the [tracking workflow](docs/planning/README.md) explains its statuses. The first real schema migration is [deferred until a source and destination version exist](https://github.com/git-maniam/API-Wrangler/issues/60). [Snapshot execution](https://github.com/git-maniam/API-Wrangler/issues/59) belongs to Phase 3, and [branch/cache invalidation](https://github.com/git-maniam/API-Wrangler/issues/23) to Phase 5. Windows 11 x64 is the current desktop target; [ARM64 is deferred](https://github.com/git-maniam/API-Wrangler/issues/53). No delivery dates are assigned.

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
