# API Wrangler

A local-first Windows API testing workbench with a shared headless engine. This repository currently implements the **phase-0 foundation**, not the complete product. See [implementation status](docs/implementation-status.md) and the [product specification](api-wrangler-prod-spec.md).

## Build and test

Install .NET SDK 10.0.403. Git is needed for Git integration tests; ordinary HTTP use does not require it.

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

Track implementation in the [GitHub Kanban](https://github.com/users/git-maniam/projects/4/views/3). See [tracking workflow](docs/planning/README.md) before starting changes.

To reproduce the Linux headless checks locally:

```powershell
docker build -f scripts/verify-linux.Dockerfile -t api-wrangler-foundation-tests .
docker run --rm api-wrangler-foundation-tests
```
