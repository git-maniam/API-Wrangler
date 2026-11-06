# Repository Guidelines

## Project Structure & Module Organization

This repository implements the phase-0 foundation. `README.md` documents local usage; `api-wrangler-prod-spec.md` defines the implementation baseline, phases, and acceptance criteria. Read the relevant specification sections and `docs/implementation-status.md` before contributing.

C# projects live in `src/ApiWrangler.*`, tests in `tests/ApiWrangler.IntegrationTests`, fixtures in `tests/Fixtures`, and documentation in `docs/`. The Node probe lives in `workers/node`. Introduce modules when useful; do not scaffold every future project upfront. The specified `api-wrangler/` directory contains user project assets, not application source.

## Build, Test, and Development Commands

Use the SDK pinned in `global.json`. Restore with `dotnet restore --locked-mode`; update dependency pins and lockfiles together. From the solution directory:

- `dotnet build`: build the solution from its directory.
- `dotnet test`: execute the solution's automated tests.
- `dotnet run --project src/ApiWrangler.Desktop`: launch the WPF desktop application on Windows.
- `dotnet format --verify-no-changes`: check formatting.
- `scripts/Test-Desktop.ps1`: verify desktop HTTP binding and cancellation after a Release build.

## Coding Style & Naming Conventions

Use four-space C# indentation, PascalCase types/public members, camelCase parameters/locals, and `Async` suffixes for asynchronous methods. Match namespaces to `ApiWrangler.*` modules. Follow `.editorconfig`; SDK analyzers and warnings-as-errors are enabled.

Use MVVM for WPF; keep the execution engine independent of the UI and I/O off the dispatcher thread. Canonical project JSON requires UTF-8, LF endings, two-space indentation, stable ordering, and a trailing newline.

## Testing Guidelines

Use xUnit and descriptive names such as `Save_UnchangedRequest_ProducesNoDiff`. Add meaningful behavior/failure tests; no numeric coverage threshold is imposed. Use local fixture services and temporary Git repositories. The main suite must not require GitHub credentials. Explicitly enable Node tests as documented in README; never report skipped tests as passed.

## Commit & Pull Request Guidelines

Existing commits use short descriptive subjects (`Initial commit`, `Product Spec Added`); no strict convention is established. Keep commits focused. PRs should describe behavior, relevant specification sections, validation performed, and remaining limitations. Link applicable issues and include screenshots for UI changes.

## GitHub Project Synchronization

Track all implementation in [project 4](https://github.com/users/git-maniam/projects/4). Use `docs/planning/github-ticket-map.json` to find issues. Set In progress before coding; post executed verification and remaining gaps. Use In review only with a reviewable PR, and Done only after verified acceptance and merge. Keep phase epics synchronized with their sub-issues. Never treat local uncommitted code as Done. See `docs/planning/README.md` and `scripts/Update-ProjectTicket.ps1`. ARM64 is deferred; the current desktop target is Windows 11 x64.

## Security & Architecture Decisions

Never commit credentials or raw local run data. Opening or importing projects must not execute scripts. Record architecture deviations in `docs/decisions/ADR-*.md`; update implementation status and requirement traceability as phases progress.
