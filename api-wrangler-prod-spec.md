# API Wrangler — Product and Implementation Specification

**Specification version:** 1.0  
**Date:** 2026-11-06  
**Primary platform:** Windows 11  
**Primary implementation:** C# / .NET  
**Product model:** Local-first desktop application with Git-backed collaboration and optional self-hosted services  
**Subscription requirement:** None  
**Status:** Implementation baseline

## 1. Product definition

API Wrangler is a Windows application for developing, executing, debugging, maintaining, and sharing API tests.

It provides the developer-facing capabilities commonly associated with a paid API testing platform:

- API request construction and inspection.
- Repeatable functional testing.
- CSV/JSON-driven testing.
- Reusable datasets and test packages.
- GraphQL and gRPC testing.
- Local and distributed performance testing.
- Mock servers.
- Webhook capture and replay.
- Scheduled monitoring.
- Browser-test integration.
- Command-line and CI execution.
- Shared collections, environments, datasets, and selected results.
- API documentation and contract validation.
- Optional AI assistance.

API Wrangler stores portable project assets in an `api-wrangler/` directory inside a Git repository.

The desktop application uses the locally installed Git executable and the authentication already configured for that Git installation. It does not require an API Wrangler account, subscription, proprietary synchronization service, or separate GitHub login.

Optional services replace hosted platform capabilities using infrastructure controlled by the team.

### 1.1 Core product principles

1. **Files are the source of truth.** Essential project assets must remain usable without the desktop application.
2. **Git provides collaboration.** Sharing occurs through commits, branches, pushes, fetches, merges, and pull requests.
3. **Offline work is first-class.** Local request construction, datasets, test execution, and result inspection must work without GitHub connectivity.
4. **The UI must remain responsive.** Network work, Git operations, parsing, indexing, and execution run outside the UI thread.
5. **Execution must be reproducible.** Runs record the project snapshot, inputs, execution configuration, and engine version.
6. **Existing local Git authentication is reused.** Credentials are handled by Git and its configured helpers.
7. **Automation and desktop execution use the same engine.**
8. **No artificial usage caps.** Practical limits arise from configured resource budgets and infrastructure capacity.
9. **No silent loss of user work.**
10. **Trusted code runs explicitly.** Opening, cloning, fetching, previewing, or importing a repository must not execute its scripts.

### 1.2 Meaning of platform parity

Parity means comparable developer outcomes, not an identical UI or complete compatibility with every proprietary Postman behavior.

Examples:

- Hosted collection execution ? self-hosted runners.
- Hosted scheduled monitoring ? self-hosted scheduler and runners.
- Hosted webhook URLs ? a team-operated public webhook gateway.
- Hosted load generation ? distributed load-generator workers.
- Shared cloud collections ? files reviewed and synchronized through Git.
- Shared secret values ? secret references resolved independently on each execution host.
- Paid AI credits ? optional user-supplied model provider credentials.

The implementation must maintain a compatibility matrix documenting supported, partially supported, and unsupported behaviors.

### 1.3 Explicit exclusions

The following are outside the product scope unless separately requested:

- Subscription billing.
- Enterprise SSO.
- Employee provisioning.
- Organization-wide device administration.
- Reimplementation of GitHub permissions.
- A commercial public API marketplace.
- A proprietary cloud account system.
- Guaranteed compatibility with every Postman feature or script.
- A full general-purpose IDE.
- A replacement for application source control.

GitHub, infrastructure, model providers, signing certificates, and other third-party services may have their own costs. API Wrangler must not describe those services as universally free.

## 2. Target users and primary workflows

### 2.1 Developer

Needs to:

- Send a request to a local or remote API.
- Inspect and compare responses.
- Debug authentication.
- Create assertions.
- Reproduce a reported failure.
- Run a relevant subset of regression tests.
- Commit test changes alongside application changes.

### 2.2 Tester or QA engineer

Needs to:

- Maintain test cases without duplicating requests.
- Paste data from spreadsheets.
- Import CSV and JSON.
- Run scenario matrices.
- Review failures by iteration.
- Share sanitized evidence.
- Maintain predictable test data.

### 2.3 Test automation engineer

Needs to:

- Run suites in CI.
- Produce JUnit, JSON, and HTML results.
- Parameterize environments.
- Integrate browser tests.
- Run scheduled tests.
- Configure distributed performance tests.

### 2.4 Team maintainer

Needs to:

- Review changes before they are shared.
- Resolve concurrent edits.
- Maintain shared helper packages.
- Evolve project schemas safely.
- Keep secrets and generated files out of inappropriate commits.

### 2.5 Required end-to-end journeys

| Journey | Required outcome |
|---|---|
| Open existing repository | Discover and load its `api-wrangler/` project |
| Clone GitHub repository | Clone with local Git credentials and open project |
| Create first request | Send, inspect, save, and rerun it |
| Build regression collection | Execute ordered requests and assertions |
| Add test cases from spreadsheet | Paste, map columns, validate, and execute |
| Share work | Review diff, commit selected changes, and push |
| Receive teammate changes | Fetch, review, integrate, and reload safely |
| Resolve conflicting dataset edits | Resolve row/cell conflicts without losing either version |
| Run in CI | Execute pinned snapshot and publish machine-readable results |
| Monitor staging | Execute on a self-hosted schedule and retain history |
| Debug webhook | Capture, forward, inspect, and replay an event |
| Validate browser traffic | Run Playwright tests and assert captured API interactions |

## 3. Technology and architecture decisions

### 3.1 Desktop UI

Default: WPF on .NET 10 LTS, using Windows 11 Fluent styling.

Rationale:

- Native Windows desktop foundation.
- Mature controls, accessibility, data binding, and tooling.
- Good fit for virtualized request trees and dataset grids.
- No browser process required for the application shell.
- Shared .NET code with CLI and services.
- Windows 11 appearance without building an Electron application.

“Blazing fast” must be demonstrated with benchmarks, not inferred from the framework.

Use the latest supported stable .NET 10 servicing release at implementation time. Pin the SDK in `global.json`.

Do not upgrade the major runtime automatically during implementation.

### 3.2 UI implementation rules

- MVVM architecture.
- `CommunityToolkit.Mvvm` or equivalent lightweight MVVM primitives.
- Native controls wherever practical.
- Virtualization for trees, lists, and grids.
- Incremental loading for large datasets.
- Deferred parsing of large bodies.
- Asynchronous commands with cancellation.
- No network or filesystem work on the dispatcher thread.
- No WebView2 dependency in the primary shell.
- Text-first response rendering.
- Optional rich preview loaded only when requested.

Prefer a native text editor component such as AvalonEdit, subject to maintenance and license review.

Do not build a custom code editor from scratch.

### 3.3 Headless engine

The engine must be independent of WPF.

The same engine supports:

- Desktop execution.
- CLI execution.
- Windows services.
- GitHub Actions.
- Optional Linux containers for self-hosted workers.

The desktop remains Windows-only. Cross-platform headless execution is a separate supported target.

### 3.4 Proposed solution layout

```text
src/
  ApiWrangler.Desktop/
  ApiWrangler.Domain/
  ApiWrangler.Application/
  ApiWrangler.Storage/
  ApiWrangler.Git/
  ApiWrangler.Execution/
  ApiWrangler.Protocols.Http/
  ApiWrangler.Protocols.GraphQl/
  ApiWrangler.Protocols.Grpc/
  ApiWrangler.Protocols.WebSocket/
  ApiWrangler.Scripting/
  ApiWrangler.ImportExport/
  ApiWrangler.Reporting/
  ApiWrangler.Cli/
  ApiWrangler.Runner/
  ApiWrangler.Scheduler/
  ApiWrangler.ControlPlane/
  ApiWrangler.WebhookGateway/
  ApiWrangler.Playwright/
  ApiWrangler.Contracts/

tests/
  ApiWrangler.UnitTests/
  ApiWrangler.IntegrationTests/
  ApiWrangler.GitTests/
  ApiWrangler.CompatibilityTests/
  ApiWrangler.UiTests/
  ApiWrangler.PerformanceTests/
  Fixtures/

docs/
  architecture/
  decisions/
  compatibility/
  operations/

schemas/
samples/
scripts/
```

Do not create empty projects for every future module in phase 0. Introduce projects when their boundaries become useful.

### 3.5 Dependency direction

```text
Desktop / CLI / Services
          |
      Application
          |
        Domain
          ^
          |
Storage / Git / Execution / Protocol adapters
```

Domain models must not depend on WPF, Git process APIs, or particular database libraries.

### 3.6 Persistence boundaries

| Data | Authoritative location |
|---|---|
| Collections and requests | Git-tracked files |
| Environment definitions | Git-tracked files |
| Dataset definitions and fixtures | Git-tracked files |
| Scripts and package manifests | Git-tracked files |
| Test definitions | Git-tracked files |
| Schedules and monitor definitions | Git-tracked files |
| Search index | Rebuildable local cache |
| Window state and preferences | Local user settings |
| Local run history | Local operational store |
| Runner queue and leases | Control-plane operational store |
| Secret values | OS/runner secret store |
| Selected shared reports | Explicitly exported Git-tracked files or external artifacts |

A database must never become the only location containing a collection, test definition, or dataset.

## 4. Project directory and file format

### 4.1 Required directory layout

```text
repository/
  application-source/

  api-wrangler/
    wrangler.json

    collections/
      checkout/
        collection.json
        folders/
          customer.folder.json
        requests/
          create-customer.request.json
          create-order.request.json
          get-order.request.json
        scripts/
          before-collection.js
          after-collection.js

    environments/
      local.environment.json
      staging.environment.json

    datasets/
      registration/
        dataset.json
        cases.csv
        columns.json

      checkout/
        dataset.json
        cases.json
        columns.json

    scripts/
      helpers/
        auth.js
        assertions.js
      package.json
      package-lock.json

    contracts/
      openapi/
      graphql/
      protobuf/
      json-schema/

    mocks/
    webhooks/
    monitors/
    schedules/
    flows/
    performance/
    applications/
    docs/
    reports/
    fixtures/

    .gitignore
    .gitattributes
    README.md
```

Support opening a repository subdirectory containing the manifest.

Version 1 supports one API Wrangler project root per selected project context. Multiple projects in a repository may be opened separately.

### 4.2 Canonical serialization

Project files must use:

- UTF-8.
- LF line endings.
- Two-space JSON indentation.
- A trailing newline.
- Stable serialization ordering.
- Stable identifiers.
- ISO 8601 UTC timestamps where timestamps are necessary.
- Culture-invariant numeric serialization.

Avoid rewriting files that have not semantically changed.

Do not update timestamps on every save.

Collections must not be serialized as a single enormous JSON document.

### 4.3 Identifiers and names

Every entity has:

- An immutable UUID.
- A human-readable name.
- A schema version.
- A deterministic file association.

Renaming an entity must not change its identity.

References use IDs, not display names or mutable filesystem paths.

File moves must not silently create new entities.

### 4.4 Manifest example

```json
{
  "$schema": "./schemas/wrangler.schema.json",
  "schemaVersion": 1,
  "projectId": "11111111-1111-4111-8111-111111111111",
  "name": "Commerce API Tests",
  "defaultEnvironmentId": "22222222-2222-4222-8222-222222222222",
  "paths": {
    "collections": "collections",
    "environments": "environments",
    "datasets": "datasets",
    "reports": "reports"
  },
  "execution": {
    "requestTimeoutMs": 30000,
    "maxConcurrentRequests": 4,
    "maxRedirects": 10,
    "tlsVerification": true
  },
  "results": {
    "defaultRetentionDays": 30,
    "shareRawBodiesByDefault": false
  }
}
```

The referenced schemas must actually be included or generated during project initialization. Do not ship broken schema links.

### 4.5 Request example

```json
{
  "schemaVersion": 1,
  "id": "33333333-3333-4333-8333-333333333333",
  "name": "Create order",
  "protocol": "http",
  "method": "POST",
  "url": "{{baseUrl}}/orders",
  "headers": [
    {
      "name": "Content-Type",
      "value": "application/json",
      "enabled": true
    }
  ],
  "query": [],
  "auth": {
    "type": "bearer",
    "token": "{{secret:apiToken}}"
  },
  "body": {
    "mode": "json",
    "text": "{\"customerId\":\"{{customerId}}\",\"sku\":\"{{sku}}\"}"
  },
  "scripts": {
    "before": [],
    "after": []
  },
  "assertions": [
    {
      "id": "44444444-4444-4444-8444-444444444444",
      "type": "status",
      "operator": "equals",
      "expected": 201
    }
  ]
}
```

Headers and query parameters are arrays because duplicates and ordering can matter.

### 4.6 Collection model

A collection defines:

- Identity and description.
- Request/folder references.
- Explicit execution order.
- Default authentication.
- Default variables.
- Before/after scripts.
- Setup and teardown references.
- Execution policies.
- Tags.

Do not rely on filesystem enumeration to determine execution order.

### 4.7 Forward compatibility and migration

- Reject unsupported newer major schema versions for editing.
- Offer read-only inspection where possible.
- Preserve unknown extension fields.
- Validate every asset before execution.
- Show errors with file, entity, and field location.
- Preview migrations.
- Back up original files before migration.
- Never migrate automatically during fetch.
- Record schema migration version and changes.
- Keep migration logic testable and idempotent.

## 5. Local-only storage

Store user-specific operational data under an application-owned directory such as:

```text
%LOCALAPPDATA%\ApiWrangler\
  settings/
  cache/
  indexes/
  runs/
  logs/
  recovery/
  trust/
```

Use SQLite for rebuildable indexes and operational run history where appropriate.

Keep large response bodies in separate bounded files rather than database blobs by default.

The repository must not contain:

- Window positions.
- Personal recent-file lists.
- Search indexes.
- Authentication tokens.
- Raw caches.
- Temporary run output.
- User-specific machine paths.

### 5.1 Repository ignores

Generate narrowly scoped ignore rules for:

```gitignore
.local/
.cache/
runs/
tmp/
*.local.environment.json
*.secrets.json
```

Do not overwrite an existing ignore file.

`reports/` remains available for intentionally shared reports. Raw execution history is not automatically committed.

## 6. Git and GitHub integration

### 6.1 Git executable

API Wrangler must invoke the installed Git executable.

Do not substitute an embedded Git implementation as the primary synchronization mechanism.

At first use:

1. Discover `git.exe`.
2. Display its path and version.
3. Permit selecting another executable.
4. Verify supported commands.
5. Detect repository root and worktree.
6. Detect branch and upstream.
7. Detect an existing merge or rebase operation.
8. Detect repository write access.

Use process argument arrays. Do not construct shell command strings from user input.

### 6.2 Authentication

For HTTPS:

- Let Git use its configured credential helper, including Git Credential Manager.
- Support the helper’s normal browser or interactive authentication flow.
- Do not read credential databases.
- Do not copy tokens into application settings.
- Do not place tokens in command-line arguments.

For SSH:

- Use the configured SSH executable, agent, keys, host aliases, and Git configuration.
- Preserve existing `core.sshCommand` behavior.
- Display actionable errors for unavailable agents or host-key problems.
- Do not bypass host-key validation.

Git authentication must not be reused as authentication for the application APIs being tested.

### 6.3 Repository entry workflows

Support:

- Open local repository.
- Clone GitHub repository.
- Initialize `api-wrangler/` in an existing repository.
- Create a new local repository.
- Open project without Git in standalone mode.
- Add or select an existing remote.

Standalone mode supports testing and editing; collaboration controls explain that Git is not configured.

### 6.4 Remote URLs

Support:

```text
https://github.com/owner/repository.git
git@github.com:owner/repository.git
```

Respect local URL rewriting and SSH aliases.

Reject unsafe command interpolation and malformed URLs.

Do not require GitHub REST API access for clone, fetch, commit, or push.

### 6.5 Git operations exposed in UI

- Status.
- Fetch.
- Compare local and remote.
- Create branch.
- Switch branch.
- Stage selected API Wrangler files.
- Review staged diff.
- Commit.
- Push.
- Fast-forward update.
- Explicit merge.
- Explicit rebase.
- Conflict resolution.
- View history.
- Restore a selected asset from history.
- Open GitHub comparison/pull-request page.

Creating a pull request may initially open the GitHub web interface. A future optional integration may use an installed authenticated GitHub CLI.

Do not infer that Git credentials automatically authorize unrelated GitHub API operations.

### 6.6 Commit scope

The default commit includes only selected files under the active `api-wrangler/` directory.

Before committing:

1. Read current Git status, including index state.
2. Detect unrelated staged changes.
3. Display the exact proposed commit contents.
4. Validate selected project assets.
5. Scan selected text changes for likely secrets.
6. Ask for a commit message.
7. Commit only the reviewed scope.

Never use `git add .`.

If unrelated changes are already staged, the initial implementation must block the scoped commit and explain how to continue without changing that index.

A later isolated-index implementation may handle this more conveniently, but must preserve the user’s real index exactly.

### 6.7 Synchronization state machine

```text
Disconnected
  -> LocalOnly
  -> Clean
  -> LocalChanges
  -> Ahead
  -> Behind
  -> Diverged
  -> Conflict
  -> OperationInProgress
  -> AuthenticationRequired
  -> Error
```

Represent independent facts separately. For example, a branch can be behind and also have uncommitted changes.

Do not reduce every state to a single misleading badge.

### 6.8 Fetch versus integration

Fetch downloads remote references. It must not modify working files.

Updating the working tree is a separate operation.

Default integration behavior:

- Clean tree and fast-forward possible ? allow fast-forward update.
- Dirty tree ? require save/commit or explicit handling.
- Diverged history ? show merge/rebase choices.
- Existing operation in progress ? show recovery options.
- Detached HEAD ? allow inspection and branch creation; do not invent an upstream.

No automatic stash/pop.

No automatic force push.

No destructive hard reset as a synchronization shortcut.

### 6.9 Shared application repository

Branch switching and merges can affect files outside `api-wrangler/`.

Before these operations:

- Explain that they affect the whole Git working tree.
- Detect unrelated dirty files.
- Block unsafe operations.
- Offer opening a separate worktree as an advanced workflow.

An API Wrangler action must not silently discard application source changes.

### 6.10 External changes

Watch project files and repository state.

- Debounce watcher events.
- Detect watcher overflow and rescan.
- Compare content hashes before rewriting.
- Prompt when a dirty editor and disk have conflicting changes.
- Reload clean editors automatically.
- Preserve recoverable editor buffers.
- Invalidate caches after branch changes.
- Suspend writes during a detected Git integration operation.

### 6.11 Git configuration and hooks

Repositories and Git configuration can invoke executable code through hooks, filters, credential helpers, and other mechanisms.

Document this behavior.

Repository browsing must not automatically invoke project-defined setup commands.

Before commit, make it clear that configured Git hooks may run.

Do not silently disable user signing or hooks to make a commit succeed.

### 6.12 Git acceptance scenarios

Required integration tests include:

- HTTPS repository with configured helper.
- SSH repository.
- Offline fetch.
- Rejected authentication.
- Clean fast-forward.
- Diverged branches.
- Push rejected because remote advanced.
- Existing staged application file.
- Dirty application source file.
- Existing merge conflict.
- Existing rebase.
- Detached HEAD.
- Git worktree.
- Paths with spaces and non-ASCII characters.
- File deletion and rename.
- CRLF/LF variations.
- Git LFS pointer without installed LFS support.

## 7. Collaboration and conflict resolution

Git is asynchronous collaboration. Version 1 does not promise simultaneous live editing.

### 7.1 Conflict behavior

Use three-way comparison with base, local, and incoming versions.

For JSON:

- Merge independent object-property edits where safe.
- Match entity collections by immutable IDs.
- Treat request headers as ordered multi-value structures.
- Report same-field edits as conflicts.
- Detect delete-versus-edit conflicts.
- Preserve unknown fields.

For scripts:

- Provide a line-oriented three-way editor.
- Do not execute scripts during conflict resolution.

For binary fixtures:

- Require choosing a version or saving both.
- Do not claim semantic merging.

### 7.2 Dataset merge identity

Editable shared datasets should include a stable row ID, normally `_caseId`.

Example:

```csv
_caseId,email,password,expectedStatus
case-001,new@example.test,ValidPassword1!,201
case-002,invalid,ValidPassword1!,400
```

Rules:

- IDs must be unique.
- Reordering rows does not create new cases.
- Independent cell edits may merge.
- Same-cell edits require resolution.
- Delete-versus-edit requires resolution.
- Missing row IDs require explicit addition or text-based conflict handling.
- Duplicate IDs block execution and merge completion.

Do not rewrite an imported dataset merely to add IDs without previewing that change.

### 7.3 Conflict completion

Before marking a conflict resolved:

- Parse the result.
- Validate its schema.
- Validate references.
- Check duplicate IDs.
- Show the final diff.
- Preserve recovery copies until the operation completes.

Git remains authoritative about whether a merge is in progress.

## 8. Request editor and response inspection

### 8.1 HTTP request capabilities

Support:

- All standard methods.
- Custom method names.
- URL and query editor.
- Repeated query parameters.
- Ordered headers.
- Enable/disable individual entries.
- JSON, XML, plain text, and binary bodies.
- Form URL encoding.
- Multipart uploads.
- File-backed bodies.
- Cookies and cookie jars.
- Redirect policy.
- Compression.
- HTTP version preference.
- Proxy configuration.
- Client certificates.
- Request timeout.
- Cancellation.
- Streaming downloads.

### 8.2 Authentication

Required:

- None.
- Basic.
- Bearer.
- API key in header or query.
- OAuth 2.0 authorization code with PKCE.
- OAuth 2.0 client credentials.
- OAuth refresh.
- Windows integrated authentication where supported.
- Client certificates.

Later compatibility additions:

- OAuth 1.0.
- AWS Signature Version 4.
- Other imported authentication modes based on demand.

Unsupported imported authentication must be flagged before execution.

### 8.3 Transport correctness

- TLS verification enabled by default.
- Per-environment certificate configuration.
- Explicit visible override for invalid certificates.
- No global silent TLS bypass.
- Authorization stripped on cross-origin redirects unless explicitly permitted.
- No automatic retries for non-idempotent operations.
- Retry policy records attempts and reasons.
- Separate DNS/connect/TLS timings only where reliably observable.
- Unavailable timing fields appear as unavailable, not fabricated values.

### 8.4 Response viewer

Provide:

- Status and protocol.
- Headers.
- Cookies.
- Body text.
- Pretty JSON/XML.
- Raw body.
- Binary save.
- Image preview.
- Response size.
- Total and available phase timings.
- Assertion results.
- Console output.
- Search.
- Copy selection.
- Copy body.
- Copy sanitized response.
- Compare against another response.
- Save as a mock example.

### 8.5 Large responses

Initial defaults:

- 5 MiB text preview limit.
- Stream larger content to disk.
- User-triggered loading beyond the preview.
- Configurable 100 MiB saved-response limit.
- Explicit override when larger content is required.
- No full-body pretty printing above the configured threshold.
- Cancellation remains available while receiving and saving.

These limits protect responsiveness; they are not licensing limits.

## 9. Variables and environments

### 9.1 Variable scopes

Resolve ordinary variable names in this order, highest priority first:

1. Request-local runtime variables.
2. Current iteration data.
3. Run overrides.
4. Selected environment.
5. Collection.
6. Project defaults.

Secret references use an explicit namespace:

```text
{{secret:apiToken}}
```

They must not be shadowed by a plain dataset column named `apiToken`.

### 9.2 Resolution rules

- Missing variables fail validation by default.
- Show the unresolved name and source location.
- Support nested substitution with a maximum depth.
- Detect circular references.
- Show a resolution inspector without revealing secrets.
- Apply substitution consistently to URL, headers, auth, body, and scripts through documented APIs.
- Preserve explicit string versus typed-value behavior.

### 9.3 Environment files

Track nonsecret defaults and secret references.

Do not track actual shared token values.

Support personal overrides stored locally.

The UI shows where each effective value originated.

Runtime mutations remain run-local unless the user explicitly saves them.

### 9.4 Dynamic values

Support deterministic helpers:

- UUID.
- Current timestamp.
- Random integer.
- Random string.
- Seeded fake data.
- Run ID.
- Iteration index.

Runs record the random seed.

Time-dependent generation is recorded where needed for reproduction.

## 10. Datasets, CSV, JSON, and clipboard workflows

### 10.1 Dataset model

A dataset consists of:

- Stable ID.
- Name and description.
- Source definition.
- Column definitions.
- Optional views.
- Validation rules.
- Row identity.
- Null/type policies.

Initial source types:

- CSV.
- JSON array of objects.
- JSON Lines.
- Inline table.

Later:

- Read-only SQL source.
- HTTP source.
- Generated source.
- Composition of other datasets.

### 10.2 CSV semantics

Support:

- Commas, tabs, semicolons, and explicit delimiters.
- Quoted fields.
- Embedded delimiters.
- Escaped quotes.
- Embedded newlines.
- UTF-8 with or without BOM.
- Encoding selection for legacy imports.
- Duplicate-header detection.
- Empty string versus null mapping.
- Explicit culture for numbers and dates.
- Leading-zero preservation.
- Preview before conversion.

Do not use `string.Split(',')` as a CSV parser.

### 10.3 JSON semantics

Support:

- Arrays of records.
- JSON Lines.
- Nested objects and arrays.
- Optional flattening.
- Column mapping.
- Type preservation.
- Missing fields distinct from explicit null.

Export must clearly document whether nesting is preserved or flattened.

### 10.4 Grid features

- Virtualized rows and columns.
- Multi-cell selection.
- Copy/paste.
- Undo/redo.
- Insert/delete rows.
- Fill down.
- Duplicate case.
- Sort/filter.
- Column pinning.
- Column type editing.
- Validation indicators.
- Bulk replace with preview.
- Search.
- Open selected case in run configuration.

Sorting a view must not silently reorder the source file.

### 10.5 Clipboard import

Recognize:

- TSV from Excel or spreadsheet applications.
- CSV text.
- JSON.
- JSON Lines.
- HTTP request text.
- cURL command.
- Structured API Wrangler objects.
- Plain text.

When formats are ambiguous, show a preview and selection.

Do not execute pasted shell commands.

Never import a pasted cURL command by running cURL.

### 10.6 Spreadsheet safety

Offer spreadsheet-safe CSV export that neutralizes formula-leading cell values such as `=`, `+`, `-`, and `@`.

Preserve original data in the native project.

Explain that spreadsheet-safe export may alter displayed cell text.

### 10.7 Results pasted from other tools

Allow pasted result records to become an external result attachment.

Record:

- Source tool.
- Import time.
- Original content hash.
- User-provided test mapping.
- Environment label if supplied.
- External execution status.

Imported results must not be represented as runs executed by API Wrangler.

### 10.8 Views and live data

Views may select columns, filter rows, and derive values.

Live-source datasets must support snapshotting before a run.

A reproducible run uses the resolved snapshot, not changing database contents throughout execution.

Database connections use read-only access where possible. Mutation queries are outside the dataset-source API.

## 11. Functional test execution

### 11.1 Execution unit hierarchy

```text
Run
  -> Collection
    -> Iteration
      -> Request execution
        -> Attempt
          -> Assertions
```

Retain these distinctions in reports.

### 11.2 Required controls

- Run request.
- Run folder.
- Run collection.
- Run selected requests.
- Run tagged requests.
- Run selected dataset cases.
- Run failed cases again.
- Run with environment.
- Override variables.
- Set concurrency.
- Stop on failure.
- Continue on failure.
- Delay between requests.
- Cancel gracefully.
- Force-stop a hung worker.

### 11.3 Default lifecycle

```text
Resolve configuration
Validate assets
Create execution snapshot
Resolve secrets
Run collection setup

For each iteration:
  Create isolated iteration scope
  Run iteration setup

  For each selected request:
    Run inherited before scripts
    Resolve request values
    Send request
    Run built-in assertions
    Run inherited after scripts
    Record result

  Run iteration teardown

Run collection teardown
Finalize reports
```

Document exact inheritance order.

Recommended before order:

```text
Project -> Collection -> Folder hierarchy -> Request
```

Recommended after order:

```text
Request -> Folder hierarchy reversed -> Collection -> Project
```

### 11.4 Isolation

- Each iteration has its own mutable variable scope.
- Concurrency does not share mutable state implicitly.
- Cookie sharing is configurable.
- Dependencies between requests are explicit.
- Parallel execution of dependent requests is prohibited unless deliberately configured.

### 11.5 Assertions

Built-in assertion types:

- Status code.
- Header presence/value.
- Cookie presence/value.
- Body contains.
- JSON path value.
- JSON Schema.
- XML XPath.
- Response-size threshold.
- Response-time threshold.
- Regex match.
- Contract validation.
- Snapshot comparison.
- Custom JavaScript.

Provide typed comparison operators.

A failed assertion and a transport error are separate outcomes.

### 11.6 Status model

Required statuses:

```text
Passed
Failed
Error
Skipped
Cancelled
TimedOut
NotRun
```

Define aggregation rules.

A cancellation must not appear as a pass.

Teardown errors must remain visible even if earlier assertions passed.

### 11.7 Flow control

Support explicit:

- Next request by ID.
- Conditional branch.
- Skip request.
- Stop iteration.
- Stop collection.
- Bounded loop.
- Poll until condition or deadline.

Every loop and poll has an execution budget.

## 12. Trusted scripting and packages

### 12.1 Execution model

Scripts are trusted code with full local access, as requested.

Use an on-demand Node.js worker for JavaScript to support:

- Standard JavaScript behavior.
- NPM packages.
- Filesystem access.
- Process execution.
- Network access.
- Compatibility adapters for imported scripts.

The core product remains .NET; Node is an execution dependency for JavaScript features.

Do not claim this worker is a security sandbox.

### 12.2 Trust lifecycle

- Opening or fetching a repository does not execute code.
- Script execution requires an explicit run or installation action.
- The first script run presents the trusted-code implication.
- Record local trust by repository identity.
- Changes to scripts or package manifests are visible before execution.
- Scheduled execution uses snapshots explicitly admitted by the runner’s trust policy.
- Background monitors do not silently begin executing newly fetched scripts.

### 12.3 Worker management

- Node starts only when required.
- Pin a supported Node LTS version.
- Provide dependency diagnostics.
- Support a configured system Node installation initially.
- Consider bundled Node after distribution and update requirements are reviewed.
- Use structured IPC.
- Apply wall-clock timeouts.
- Kill the worker process tree on forced cancellation.
- Use Windows Job Objects where appropriate.
- Do not log secret values in IPC traces.
- Bound console output.

Full local access means a script can read local files and credentials available to its user. Hosted workers should therefore use dedicated execution accounts.

### 12.4 Native script API

Example:

```javascript
wrangler.test("Order created", () => {
  wrangler.expect(response.status).toEqual(201);
});

const order = response.json();
variables.set("orderId", order.id);
```

Expose:

- Request context.
- Response context.
- Variables.
- Iteration data.
- Assertions.
- Logging.
- Secondary HTTP requests.
- Flow-control commands.
- Dataset access.
- Secret references through explicit resolution.
- Run metadata.

### 12.5 Postman compatibility

Provide a documented adapter for common APIs:

- `pm.test`.
- `pm.expect`.
- `pm.response`.
- `pm.request`.
- `pm.environment`.
- `pm.collectionVariables`.
- `pm.variables`.
- `pm.iterationData`.
- `pm.sendRequest`.
- Supported execution-control calls.

Unsupported APIs produce actionable errors.

Do not silently ignore unsupported scripts.

Imported scripts retain provenance.

### 12.6 Packages

- Standard `package.json`.
- Committed lockfile.
- Deterministic installation with `npm ci`.
- No automatic installation on repository open.
- No automatic dependency lifecycle scripts by default.
- Explicit package-install action when required.
- Private registry authentication uses host configuration, not committed tokens.
- Local helper modules are supported without NPM.
- Script dependencies and versions appear in run metadata.

## 13. Import, export, and compatibility

### 13.1 Required imports

- Postman Collection v2.1.
- Postman environments.
- OpenAPI 3.0/3.1.
- cURL.
- HAR.
- CSV.
- JSON datasets.
- JSON Schema.
- GraphQL SDL.
- Protobuf definitions.

Later imports may include other client formats based on demand.

### 13.2 Import transaction

1. Read source without executing it.
2. Parse and validate.
3. Show entity counts.
4. Show secrets requiring local storage.
5. Show unsupported features.
6. Preview file changes.
7. Import into a temporary location.
8. Validate references.
9. Publish atomically into the project.
10. Save an import report.

Never partially overwrite a valid project after an import failure.

### 13.3 Required exports

- Native API Wrangler project.
- Selected collections.
- Compatible Postman v2.1 collections.
- Environment definitions without secrets.
- CSV and JSON datasets.
- JUnit XML.
- JSON results.
- Static HTML results.
- Markdown summaries.
- OpenAPI-derived documentation.

Export reports must list unsupported or lossy conversions.

Round-trip fidelity is required only for the supported compatibility subset.

## 14. Results, debugging, and evidence

### 14.1 Result envelope

Every run records:

- Run ID.
- Start/end UTC time.
- Execution duration.
- Engine version.
- Operating system and architecture.
- Project ID.
- Git commit where available.
- Dirty working-tree indicator.
- Snapshot content hash.
- Environment identity.
- Redacted override names.
- Dataset hash.
- Random seed.
- Selected request IDs.
- Script/package versions.
- Execution configuration.
- Aggregate outcome.
- Individual outcomes.

### 14.2 Snapshot semantics

For a committed clean project, record the commit SHA.

For local uncommitted work, create a content-addressed execution snapshot.

A dirty flag alone is insufficient for reproducibility.

Do not include the secret values in snapshot hashes intended for public sharing.

### 14.3 Result browser

Provide:

- Run summary.
- Failure-only filter.
- Request/iteration grouping.
- Dataset row used.
- Assertion details.
- Request/response inspection.
- Console.
- Attempt history.
- Timing.
- Diff against previous run.
- Rerun selected case.
- Copy bug-report summary.
- Export sanitized evidence.

### 14.4 Baseline comparison

Compare:

- Status.
- Headers.
- JSON structure and values.
- Text.
- Performance metrics.

Support ignored fields such as timestamps and generated IDs.

Baseline changes require explicit acceptance and produce a Git diff.

### 14.5 Sharing and retention

Default raw results stay local.

To share:

1. Select result or run.
2. Choose summary or detailed evidence.
3. Preview redaction.
4. Export under `reports/` or an artifact destination.
5. Review the Git diff separately.

Do not commit every run automatically.

Git is suitable for selected reports, not unlimited high-volume telemetry.

## 15. Secrets and sensitive test data

### 15.1 Secret values

Desktop secrets use Windows-protected local storage.

The repository stores names and references:

```json
{
  "apiToken": {
    "kind": "secret",
    "reference": "secret:staging-api-token"
  }
}
```

Other testers map that reference to their own authorized secret.

Hosted runners resolve references from their own configured secret source.

Local desktop secrets are not copied to GitHub or runners automatically.

### 15.2 Redaction

Redact configured secrets from:

- Logs.
- Reports.
- Clipboard exports.
- Request previews.
- Diagnostics.
- Error messages.
- HTML artifacts.

Redaction is best-effort for transformed values. Do not promise detection of every encoded or derived secret.

Raw export requires a deliberate action and clear preview.

### 15.3 Test-data classification

Datasets may contain sensitive information.

Support project-level guidance and warnings before sharing such files.

Prefer synthetic fixtures.

Do not prevent legitimate local testing merely because a field resembles personal information.

## 16. Protocol expansion

### 16.1 GraphQL

Support:

- Query/mutation documents.
- Variables.
- Operation selection.
- Schema import.
- Optional introspection.
- Schema-aware completion.
- Error assertions.
- Collection execution.
- Data-driven operations.
- Contract validation.

### 16.2 gRPC

Support:

- Protobuf import and include paths.
- Server reflection where enabled.
- Unary calls.
- Metadata.
- TLS and client certificates.
- Status/trailer assertions.
- Server streaming.
- Client streaming.
- Bidirectional streaming.
- Deadlines and cancellation.

Streaming calls require explicit transcript and completion semantics.

Do not shoehorn them into an HTTP response model.

### 16.3 WebSocket and SSE

Support:

- Connect/disconnect.
- Authentication.
- Send message.
- Await message with predicate.
- Timeouts.
- Transcript.
- Assertions.
- Reconnection policy.
- Bounded message retention.

SSE should use a streaming HTTP implementation.

### 16.4 Additional protocols

MQTT and other protocol adapters may follow the core platform release.

Track them as roadmap items rather than falsely claiming complete parity at initial launch.

## 17. Mock servers

### 17.1 Local mock hosting

Use an ASP.NET Core process bound to loopback by default.

Support:

- Static responses.
- Matching by method/path/query/header/body.
- Priority and specificity.
- Status/header/body configuration.
- Delays.
- Stateful scenarios.
- Dataset-driven responses.
- Scripted responses.
- Request logs.
- Reset state.
- Import from saved responses.
- OpenAPI example generation.

### 17.2 Matching

Define deterministic precedence.

Ambiguous matches appear in diagnostics.

Unknown requests return a configurable response with match explanation.

### 17.3 Fault simulation

Support:

- HTTP errors.
- Delayed responses.
- Connection termination where transport allows.
- Malformed payloads.
- Rate-limit scenarios.
- Intermittent failures using a recorded random seed.

### 17.4 Shared mocks

The same definitions run on a self-hosted service.

Remote exposure requires explicit network binding and authentication configuration.

Starting a desktop mock must not automatically expose it to the internet.

## 18. Webhook gateway

A stable public webhook URL requires a publicly reachable service. GitHub storage alone cannot provide it.

### 18.1 Components

- Local listener.
- Optional public gateway.
- Desktop connection to gateway.
- Event store.
- Forwarding/replay engine.

### 18.2 Required behavior

- Stable listener IDs.
- Raw header/body capture.
- Arrival timestamp.
- Provider signature validation.
- Custom response.
- Forward to local or remote target.
- Replay original payload.
- Edit-and-replay as a distinct event.
- Duplicate-event testing.
- Search/filter.
- Export sanitized event.

### 18.3 Fidelity

Preserve raw bytes used for signature verification.

Do not parse and reserialize a payload before verifying its signature.

### 18.4 Operational controls

- Listener authentication.
- Body-size limits.
- Rate limits.
- Retention.
- Connection timeouts.
- Forwarding destination policy.
- Replay audit records.
- Secret references.
- TLS termination instructions.

The gateway must not become an unrestricted public forwarding proxy.

### 18.5 Git relationship

Listener definitions live in Git.

Captured events remain operational data unless explicitly exported.

Provider signing secrets stay outside Git.

## 19. Performance and distributed load testing

### 19.1 Local mode

Support:

- Fixed virtual users.
- Ramp-up.
- Spike.
- Step load.
- Duration.
- Request rate cap.
- Dataset distribution.
- Think time.
- Setup/teardown.
- Thresholds.
- Cancellation.

### 19.2 Metrics

Record:

- Requests.
- Completed iterations.
- Throughput.
- Errors by category.
- Timeouts.
- Latency histogram.
- p50/p90/p95/p99.
- Active users.
- Load-generator resource usage.
- Dropped scheduled work where applicable.

Do not calculate global percentiles by averaging worker percentiles.

Merge compatible histograms.

### 19.3 Execution semantics

Distinguish:

- Closed-loop virtual-user model.
- Open-loop arrival-rate model.

Implement closed-loop first.

Document coordinated-omission implications and load-generator limits.

### 19.4 Distributed mode

The coordinator:

1. Resolves a pinned project snapshot.
2. Divides load across workers.
3. Distributes input slices.
4. Starts the test.
5. Collects metrics.
6. Aggregates compatible histograms.
7. Tracks missing workers.
8. Applies thresholds.
9. Produces a report.

Worker clock differences must not corrupt latency measurements. Use monotonic clocks for durations.

### 19.5 Safety and realism

- Explicit target confirmation for large runs.
- Default concurrency and rate limits.
- Stop on configured target-error thresholds.
- Identify generator saturation.
- Report actual achieved load.
- Never claim the requested load was achieved if it was not.

These are execution controls, not subscription restrictions.

## 20. Hosted execution replacement

### 20.1 Supported deployment modes

1. Desktop execution.
2. Windows unattended runner.
3. Containerized runner.
4. GitHub Actions runner.
5. Distributed runner pool.

No proprietary API Wrangler hosting is required.

### 20.2 Control-plane responsibilities

- Runner registration.
- Job queue.
- Capability labels.
- Execution leases.
- Cancellation.
- Status.
- Artifact metadata.
- Retention policies.
- Schedule dispatch.
- Authentication between services.

Use SQLite for a single-node deployment.

A multi-node operational database is a later scaling option, not a prerequisite.

### 20.3 Runner protocol

Workers poll outbound for jobs by default.

Each job includes:

```text
Job ID
Project ID
Repository reference
Exact commit SHA
Project subdirectory
Execution type
Selected entities
Environment reference
Input snapshot reference
Timeout/resource limits
Artifact policy
```

Do not include plaintext long-lived credentials in the job document.

### 20.4 Reproducibility

A runner checks out an exact commit in an isolated execution directory.

It must not run “whatever is currently on main” after job creation.

Dependency installation follows committed lockfiles.

Record the runner and engine version.

### 20.5 Delivery semantics

Assume at-least-once job dispatch.

Use leases and heartbeats.

A crashed job must not be silently retried if tests may have changed external state.

Mark its outcome indeterminate and apply the configured retry policy.

Exactly-once external API effects cannot be guaranteed.

### 20.6 Host authentication

Desktop runs use local Git authentication.

Unattended hosts require their own configured authentication:

- Their own Git credential helper.
- SSH/deploy key.
- GitHub Actions token.
- Other explicitly configured machine identity.

Desktop credentials must not be extracted and uploaded to make unattended execution work.

### 20.7 Runner artifacts

Support:

- Local filesystem.
- Shared storage.
- GitHub Actions artifacts.
- Optional S3-compatible storage.

Git tracks definitions and selected reports. Operational artifacts do not require continuous commits.

### 20.8 Service authentication

Even without enterprise SSO, exposed runner services require authentication.

Initial implementation may use scoped enrollment and service tokens.

Support token rotation, revocation, TLS, and separate permissions for job submission and runner registration.

This protects the execution service; it is not an enterprise identity-management feature.

## 21. Scheduling and monitoring

### 21.1 Schedule definitions

Schedules live in Git and define:

- Collection.
- Environment.
- Dataset/view.
- Cron or interval.
- IANA time zone.
- Runner labels.
- Timeout.
- Overlap policy.
- Retry policy.
- Notification routes.
- Enabled state.

### 21.2 Execution ownership

A schedule is enabled on a selected scheduler deployment.

Opening the same repository on three laptops must not start three independent copies of every monitor.

Desktop scheduling is opt-in and clearly labeled as dependent on that machine remaining available.

### 21.3 Time semantics

- Store timestamps in UTC.
- Retain intended schedule time zone.
- Document daylight-saving behavior.
- Define missed-run behavior.
- Default to skipping missed intervals after downtime rather than creating an uncontrolled backlog.
- Prevent overlap by default.

### 21.4 Source update policy

A scheduler may:

- Remain pinned to an approved commit.
- Track a branch with explicit admission rules.

Fetching new code and authorizing scheduled execution are separate operations.

### 21.5 Notifications

Support generic HTTP notification webhooks first.

Later adapters may support email or team messaging.

Notification credentials remain outside Git.

Implement deduplication, cooldowns, recovery messages, and delivery diagnostics.

## 22. Playwright and application testing

### 22.1 Integration model

Create an NPM package that integrates with existing Playwright suites.

It captures relevant network activity and sends records to the API Wrangler validation engine.

### 22.2 Required capabilities

- Associate app tests with an API Wrangler project.
- Match captured requests to collection definitions.
- Validate responses.
- Identify unmatched traffic.
- Build dependency maps.
- Report UI scenario and API assertion together.
- Produce CI artifacts.
- Bootstrap draft requests from captured traffic.

### 22.3 Matching

Use explicit rules based on:

- Origin.
- Method.
- Path templates.
- Selected headers.
- Operation identifiers where available.

Ambiguous matches must be visible.

Do not match only by a fragile display name.

### 22.4 Constraints

- Large bodies are bounded.
- Browser caching affects observable traffic.
- Service workers and streaming require dedicated support.
- WebSocket capture is a separate capability.
- HTTPS interception is not required for Playwright’s own supported network APIs.

Captured credentials and cookies must be redacted before reporting.

### 22.5 Acceptance example

A browser checkout test passes its UI assertions.

A captured pricing response violates a configured API assertion.

The combined execution must surface the API failure and identify the browser scenario responsible.

## 23. Visual flows

Provide a visual workflow editor backed by a text definition.

Node types:

- Request.
- Condition.
- Loop.
- Delay.
- Script.
- Dataset iterator.
- Parallel group.
- Join.
- Assertion.
- Subflow.
- Output.

Requirements:

- Stable node IDs.
- Explicit edges.
- Validation for unreachable nodes.
- Bounded loops.
- Cancellation.
- Debug stepping.
- Inspect variables.
- CLI execution.
- Same engine as collection workflows where practical.

Canvas coordinates should be stored separately from execution semantics to reduce Git conflicts.

Do not build a second incompatible test engine solely for the flow editor.

## 24. Contracts, documentation, and discovery

### 24.1 Contract support

- OpenAPI linting.
- Request/response validation.
- JSON Schema validation.
- GraphQL schema validation.
- Protobuf-based checks.
- Breaking-change comparison.
- Link requests to contract operations.

### 24.2 Documentation

Generate static HTML and Markdown from project assets.

Include:

- Request descriptions.
- Parameters.
- Examples.
- Response examples.
- Authentication instructions without secret values.
- Dataset descriptions where appropriate.

Support publishing through user-operated static hosting or GitHub Pages.

### 24.3 Local API catalog

Provide searchable discovery across opened projects.

Index:

- Collections.
- Requests.
- Tags.
- Contracts.
- Owners as descriptive metadata.
- Recent test state.
- Dependencies.

The index is rebuildable.

Do not require a central database to find requests.

### 24.4 SDK generation

Provide an adapter to established generators, such as OpenAPI Generator, rather than writing a general SDK generator from scratch.

Pin generator versions and templates.

Treat generated code as a separate output directory.

Show commands and generated-file changes.

## 25. Optional AI

AI is optional and disabled until configured.

Support:

- User-provided model API key.
- Configurable compatible endpoint.
- Local model endpoint where compatible.
- Explicit context selection.
- Test drafting.
- Response explanation.
- Dataset generation.
- Import repair suggestions.
- Script refactoring.

Generated changes appear as a reviewable diff.

AI must not:

- Commit or push automatically.
- Execute generated scripts automatically.
- Upload the entire repository by default.
- Receive Git authentication credentials.
- Treat generated assertions as verified correctness.

Provider billing is outside API Wrangler.

The product must remain fully useful without AI.

## 26. Desktop UX specification

### 26.1 Main layout

```text
Top:
  Project | Environment | Search | Git status | Run

Left:
  Collections
  Datasets
  Environments
  Contracts
  Mocks
  Monitors
  Flows
  Applications

Center:
  Tabbed editors

Bottom:
  Results | Console | Problems | Git

Right, optional:
  Variables | Inspector | Documentation
```

### 26.2 Interaction principles

- Keyboard-first operation.
- Command palette.
- Configurable shortcuts.
- Light/dark/system themes.
- Clear unsaved indicators.
- Persistent tabs.
- Split request/response view.
- Search across requests, scripts, and datasets.
- Accessible focus order.
- Screen-reader labels.
- High-DPI support.
- High-contrast support.
- Reduced-animation option.

### 26.3 Essential shortcuts

Suggested defaults:

| Action | Shortcut |
|---|---|
| Send request | Ctrl+Enter |
| Save | Ctrl+S |
| Search project | Ctrl+Shift+F |
| Command palette | Ctrl+Shift+P |
| New request | Ctrl+N |
| Close tab | Ctrl+W |
| Run collection | Configurable |
| Show Git changes | Configurable |

Avoid overriding standard text-editing shortcuts.

### 26.4 Error presentation

Every actionable error should provide:

- What failed.
- Which asset or operation failed.
- Relevant technical details.
- Suggested next action.
- Copy diagnostics.

Do not expose secrets in copied diagnostics.

## 27. Performance requirements

These are acceptance targets, not guaranteed claims before measurement.

### 27.1 Reference machine

Measure Release builds on a documented Windows 11 machine with:

- 4 or more modern CPU cores.
- 16 GB RAM.
- NVMe SSD.
- No debugger attached.
- Local fixture API for transport benchmarks.

Record actual hardware and OS build.

### 27.2 Initial targets

| Metric | Target |
|---|---:|
| Warm start to usable shell | p95 ? 1.5 seconds |
| Cold start to usable shell | p95 ? 3 seconds |
| Open indexed project with 10,000 requests | p95 ? 2 seconds |
| Search indexed 10,000 requests | p95 ? 150 ms |
| Switch ordinary editor tabs | p95 ? 100 ms |
| Initial dataset view, 100,000 rows | ? 2 seconds |
| Idle desktop private memory, ordinary project | ? 200 MiB |
| Normal interaction | No repeated UI stalls above 100 ms |

Measure desktop, Node workers, and runner processes separately and together.

### 27.3 Implementation controls

- Lazy-load collection content.
- Virtualize dataset grids.
- Cache parsed assets by content hash.
- Bound in-memory response retention.
- Batch UI result updates.
- Use channels with bounded capacity.
- Apply backpressure to logging.
- Stream CSV imports.
- Avoid eagerly deserializing every dataset.
- Stop background indexing when no longer needed.
- Profile before adding complexity.

## 28. CLI contract

Executable name:

```text
api-wrangler
```

Optional short alias:

```text
awr
```

### 28.1 Required commands

```bash
awr init
awr validate --project ./api-wrangler

awr run \
  --project ./api-wrangler \
  --collection checkout \
  --environment staging \
  --dataset registration \
  --reporters console,junit,json \
  --output ./artifacts

awr mock serve --project ./api-wrangler --mock checkout

awr performance run \
  --project ./api-wrangler \
  --plan checkout-load

awr runner start --config runner.local.json

awr schedule validate --project ./api-wrangler

awr import postman --file collection.json --project ./api-wrangler

awr export postman --collection checkout --output checkout.json

awr docs build --project ./api-wrangler --output ./site
```

### 28.2 CLI rules

- Noninteractive mode.
- Secret references through environment or configured stores.
- Cancellation handling.
- Structured JSON output.
- Stable error codes.
- No mandatory desktop installation.
- No automatic Git push.
- No interactive authentication in CI unless explicitly allowed.

### 28.3 Exit codes

| Code | Meaning |
|---:|---|
| 0 | Execution passed |
| 1 | Assertions failed |
| 2 | Invalid configuration or project |
| 3 | Execution infrastructure error |
| 4 | Cancelled or interrupted |
| 5 | Required dependency unavailable |

Document mapping when several conditions occur.

## 29. CI integration

Provide examples for:

- GitHub Actions.
- Generic shell-based CI.
- Windows self-hosted runner.
- Linux container runner.

Default GitHub workflow:

1. Check out repository.
2. Install pinned API Wrangler CLI.
3. Restore script dependencies when needed.
4. Validate project.
5. Resolve CI secrets.
6. Run tests.
7. Publish JUnit and HTML artifacts.
8. Return correct exit status.

Do not run untrusted pull-request scripts with privileged repository secrets.

Do not recommend `pull_request_target` as a shortcut for executing arbitrary contributed tests.

CI must retain artifacts even when tests fail.

## 30. Reliability and recovery

### 30.1 File writes

- Write to a temporary file on the same volume.
- Flush.
- Validate where practical.
- Replace atomically.
- Retain bounded recovery copies.
- Detect external modifications before replacing.

Multi-file operations use a journal so interrupted imports can be recovered.

### 30.2 Concurrency

- One application writer per project by default.
- Second instance offers read-only mode or a separate worktree.
- Serialize application-initiated Git mutations.
- Respect Git’s own lock failures.
- Never delete `.git/index.lock` automatically.

### 30.3 Crash recovery

Recover:

- Unsaved text.
- Dataset edits.
- Interrupted import state.
- Partially finalized local results.

Do not resume a mutating API request automatically after a crash.

### 30.4 Diagnostics

- Structured logs.
- Rotating retention.
- Redaction.
- Correlation IDs.
- Exportable diagnostic bundle.
- No telemetry required for operation.
- Any optional telemetry is explicit and documented.

## 31. Testing and verification strategy

### 31.1 Unit tests

Focus on meaningful logic:

- Variable precedence.
- URL/body substitution.
- Assertion semantics.
- Serialization stability.
- Dataset typing.
- Three-way merges.
- Schedule calculations.
- Result aggregation.
- Histogram merging.
- Redaction.

### 31.2 Integration tests

Use local fixture services for:

- Authentication.
- Redirects.
- Cookies.
- Multipart uploads.
- Large bodies.
- Compression.
- Timeouts.
- TLS failures.
- gRPC.
- Streaming.
- Webhooks.
- Mock matching.
- Cancellation.

### 31.3 Git tests

Use temporary repositories and local bare remotes.

Do not require real GitHub credentials for the main automated test suite.

Add optional manual GitHub HTTPS/SSH verification.

### 31.4 Compatibility tests

Maintain imported fixtures for:

- Supported Postman scripts.
- Collection v2.1.
- Environment files.
- OpenAPI variants.
- cURL variants.
- CSV quoting and encodings.

Each unsupported case should have a deliberate diagnostic.

### 31.5 Property-based and fuzz tests

Apply to:

- CSV.
- JSON import.
- cURL parsing.
- Variable interpolation.
- Path validation.
- Conflict resolution.
- Malformed protocol input.

### 31.6 UI tests

Cover critical journeys rather than every visual property:

- Create/send/save request.
- Paste dataset.
- Execute collection.
- Inspect failing case.
- Commit selected changes.
- Resolve conflict.
- Cancel execution.
- Recover unsaved work.

### 31.7 Release verification

- Clean installation.
- Upgrade preserving user data.
- Offline operation.
- CLI without desktop.
- Dependency diagnostics.
- x64 validation.
- ARM64 validation when shipping ARM64.
- Performance benchmarks.
- Dependency/license review.

## 32. Implementation phases

No fixed delivery dates are assigned. Estimates should follow the initial technical spikes.

Each phase must produce a runnable increment, tests, documentation, and a concise completion report.

### Phase 0 — Architecture and feasibility

**Build:**

- Minimal WPF Fluent shell.
- Minimal headless engine.
- SDK and dependency pinning.
- Request-editor prototype.
- Virtualized dataset prototype.
- Git process wrapper prototype.
- Node IPC prototype.
- Architecture decision records.

**Investigate:**

- 100,000-row grid behavior.
- Startup and idle memory.
- Native editor component.
- Git authentication interaction.
- Cancellation of process trees.
- Script compatibility feasibility.

**Exit criteria:**

- UI targets have baseline measurements.
- HTTP request executes outside UI thread.
- Node worker runs and cancels.
- Local Git fetch works with existing authentication.
- Major dependencies have acceptable licenses.
- Architecture decisions are recorded.

Do not begin distributed infrastructure before these decisions are settled.

### Phase 1 — Project format and persistence

**Build:**

- Manifest and entity schemas.
- Stable IDs.
- Canonical serializers.
- Project initialization.
- Discovery and loading.
- Validation.
- Atomic saves.
- Local settings.
- File watcher.
- Recovery store.

**Exit criteria:**

- Save/load round trip preserves meaning.
- Repeated save produces no diff.
- Invalid files produce useful diagnostics.
- Interrupted write does not destroy the previous valid file.
- External edits are detected.

**Implementation checkpoint (2026-11-07):** Project initialization/discovery, schema validation, canonical JSON, writer ownership, transaction journals, watching, snapshots, and local settings are implemented in the phase-1 foundation. Windows x64 integration checks cover newer-schema read-only inspection, nested repository discovery and file-and-field diagnostics for missing required properties, extension-field preservation, no-diff repeated saves, interrupted multi-file recovery, second-writer read-only access, external-edit conflicts, and snapshot metadata. Latest integration run: 26 passed and four optional Node tests skipped because the worker is not configured. Local settings persistence, dirty-draft preservation through external changes, debounced watcher notification, and recovery conflict preservation have integration checks; P1.1-P1.3 remain In progress pending full migration/backup acceptance, branch/cache invalidation, and final phase review. Dirty-draft conflict, watcher overflow full-rescan signaling, and in-process transaction rollback, idempotent recovery, and third-party edit preservation are verified. Resume with those remaining cases before Phase 2. See docs/implementation-status.md and GitHub issues 8-10.\n\n### Phase 2 — HTTP testing workbench

**Build:**

- Request tree and tabs.
- HTTP request editor.
- Variables and environments.
- Basic/Bearer/API-key authentication.
- Response viewer.
- Cookies.
- Timeouts/cancellation.
- Local secrets.
- Request history.

**Exit criteria:**

- Developer can create, send, save, close, reopen, and rerun a request.
- Multipart and file-backed bodies work.
- Large response does not freeze the UI.
- TLS verification is enforced by default.
- No secrets appear in standard logs.

### Phase 3 — Collections and assertions

**Build:**

- Collection/folder hierarchy.
- Ordered execution.
- Built-in assertions.
- Setup/teardown.
- Iteration isolation.
- Failure inspection.
- Snapshot metadata.
- JSON/JUnit reports.
- Minimal CLI.

**Exit criteria:**

- A CRUD workflow passes end to end.
- Failed assertion produces exit code 1.
- Transport failure remains distinguishable.
- Cancellation propagates through the run.
- Desktop and CLI produce equivalent outcomes.

### Phase 4 — Datasets and clipboard

**Build:**

- CSV/JSON/JSONL parser adapters.
- Import wizard.
- Virtualized grid.
- Paste from spreadsheets.
- Type/null policies.
- Dataset row IDs.
- Data-driven execution.
- Export.
- External-result attachments.

**Exit criteria:**

- Quoted multiline CSV round trips correctly.
- Leading-zero values are preserved.
- At least 100,000 rows can be viewed without eager UI allocation.
- Every failed iteration identifies its case.
- Import errors do not partially overwrite assets.

### Phase 5 — Git collaboration

**Build:**

- Git discovery.
- Clone/open repository.
- Status/fetch.
- Diff.
- Scoped stage/commit.
- Push.
- Branch handling.
- Fast-forward updates.
- Conflict UI.
- Dataset-aware merging.
- GitHub comparison links.

**Exit criteria:**

- Two independent clones collaborate through a bare test remote.
- Same-cell conflict requires a decision.
- Independent changes merge safely.
- Unrelated staged files are preserved.
- Rejected push loses no work.
- External branch changes invalidate caches correctly.

**Milestone A:** usable team HTTP regression-testing product.

### Phase 6 — Scripting, packages, and Postman migration

**Build:**

- Trusted Node worker.
- Native script API.
- Common `pm.*` adapter.
- Local helper packages.
- Lockfile-based installation.
- Postman v2.1 import/export.
- Environment migration.
- cURL/OpenAPI imports.
- Compatibility report.

**Exit criteria:**

- Supported real-world Postman fixtures execute correctly.
- Unsupported APIs fail visibly.
- Infinite-loop script can be terminated.
- Opening/importing does not execute code.
- Dependency installation is explicit.
- Private registry credentials never enter project files.

### Phase 7 — GraphQL, gRPC, and streaming

**Build:**

- GraphQL editor and collection execution.
- gRPC import/reflection.
- Unary and streaming calls.
- WebSocket/SSE sessions.
- Protocol-specific assertions and reports.

**Exit criteria:**

- Local fixture suites pass for each protocol.
- Streaming deadlines and cancellation work.
- Reports retain correct protocol semantics.
- Unsupported export conversions are disclosed.

### Phase 8 — Mocks and webhook debugging

**Build:**

- Local mock server.
- Dataset/script-driven mocks.
- Fault injection.
- Local webhook listener.
- Public gateway service.
- Forwarding.
- Signature verification.
- Replay.

**Exit criteria:**

- Captured event replays byte-faithfully.
- Signature validation operates on raw payload.
- Mock routing precedence is deterministic.
- Local mock binds to loopback by default.
- Gateway has limits, authentication, and retention.

### Phase 9 — Local performance testing

**Build:**

- Load-plan files.
- Virtual-user execution.
- Load profiles.
- Histograms.
- Thresholds.
- Resource monitoring.
- Performance reports.

**Exit criteria:**

- Requested versus achieved load is reported.
- Generator saturation is visible.
- Percentile aggregation is tested.
- Stop/cancel does not leave orphaned workers.
- Functional and performance outcomes are distinguishable.

### Phase 10 — Self-hosted execution and monitoring

**Build:**

- Runner service.
- Control plane.
- Enrollment/authentication.
- Queue/leases.
- Pinned checkout.
- Artifact handling.
- Scheduler.
- Notifications.
- GitHub Actions examples.

**Exit criteria:**

- Scheduled suite runs with desktop closed.
- Runner uses its own Git and secret configuration.
- Exact commit is recorded.
- Worker crash produces a visible indeterminate outcome.
- Schedule overlap policy works.
- Multiple desktop clients do not duplicate schedules.

### Phase 11 — Distributed load and hosted mocks

**Build:**

- Worker-pool coordination.
- Distributed input allocation.
- Aggregated metrics.
- Hosted mock deployments.
- Failure handling.
- Deployment documentation.

**Exit criteria:**

- Multi-worker test produces correct merged metrics.
- Worker loss is shown.
- Runs do not silently continue against different snapshots.
- Hosted mocks use the same definitions as local mocks.

### Phase 12 — Playwright and visual workflows

**Build:**

- Playwright capture package.
- Request matching.
- API assertions on browser traffic.
- Dependency visualization.
- Combined reports.
- Text-backed flow editor.
- Flow debugging.

**Exit criteria:**

- Passing UI test can surface a failing API assertion.
- Captured secrets are redacted.
- Ambiguous matching is reported.
- Flow and collection execution share reusable engine primitives.

### Phase 13 — Documentation, optional AI, and release hardening

**Build:**

- Static docs.
- Contract diffs.
- Local catalog.
- SDK generator integration.
- Optional AI provider adapter.
- Installer.
- Upgrade path.
- Performance tuning.
- Operational manuals.
- Compatibility documentation.

**Exit criteria:**

- Full targeted developer-capability matrix is reviewed.
- No critical placeholder remains in shipped functionality.
- Fresh-machine installation succeeds.
- CLI and unattended deployment succeed.
- Performance targets are measured.
- AI is not required.
- Project data survives upgrade.
- Rollback instructions exist.

**Milestone B:** full targeted developer-platform release.

## 33. Capability acceptance matrix

| Capability | First delivery | Full acceptance |
|---|---|---|
| HTTP client | Phase 2 | Authentication, files, redirects, TLS, inspection |
| Collection regression | Phase 3 | Deterministic lifecycle and assertions |
| CSV/JSON testing | Phase 4 | Clipboard, types, validation, iteration evidence |
| GitHub collaboration | Phase 5 | Existing auth, safe scope, conflicts |
| Reusable scripts | Phase 6 | Node packages and compatibility diagnostics |
| GraphQL/gRPC | Phase 7 | Automated suites and protocol-correct results |
| Mocks/webhooks | Phase 8 | Local and gateway workflows |
| Local performance | Phase 9 | Load profiles, thresholds, credible metrics |
| Hosted execution | Phase 10 | Pinned jobs and independent runner credentials |
| Monitoring | Phase 10 | Scheduler, retention, notifications |
| Distributed load | Phase 11 | Multi-worker coordination and aggregation |
| Playwright | Phase 12 | Browser traffic validation |
| Visual flows | Phase 12 | Text-backed, debuggable execution |
| Docs/contracts/AI | Phase 13 | Optional adapters and reviewable output |

A capability is not complete merely because its button exists.

## 34. Instructions for Codex implementation

### 34.1 Working procedure

For each phase:

1. Read this specification.
2. Inspect repository instructions.
3. Identify the phase’s requirements.
4. Implement the smallest complete vertical slice.
5. Add meaningful tests for behavior and failure cases.
6. Run relevant checks.
7. Update documentation.
8. Record deviations and decisions.
9. Produce a completion report.
10. Continue to the next phase only after exit criteria pass or a blocker is explicitly documented.

### 34.2 Implementation constraints

- Do not build a decorative prototype and call it complete.
- Do not mock core Git operations in production code.
- Do not store canonical project assets only in SQLite.
- Do not embed credentials in Git files.
- Do not run pasted commands.
- Do not use automatic force pushes.
- Do not commit unrelated repository changes.
- Do not fake distributed execution.
- Do not report a test as passed when it did not execute.
- Do not silently ignore unsupported imports.
- Do not replace unresolved functionality with success-returning stubs.
- Do not create an Electron or browser-shell application without an approved architecture change.
- Do not change runtime major versions casually.

### 34.3 Required living documents

Maintain:

```text
docs/implementation-status.md
docs/requirements-traceability.md
docs/compatibility/postman.md
docs/architecture/system-overview.md
docs/operations/runner-deployment.md
docs/operations/webhook-gateway.md
docs/operations/backup-and-recovery.md
docs/decisions/ADR-*.md
```

### 34.4 Completion report format

```markdown
## Completed

- Implemented behavior.

## Verified

- Commands and meaningful scenarios checked.

## Remaining

- Unimplemented requirements and known limitations.

## Decisions

- Architecture choices and rationale.

## Next

- Concrete next implementation slice.
```

### 34.5 Initial Codex prompt

```text
Implement API Wrangler according to API-Wrangler-Spec.md.

Start with Phase 0, then Phase 1. Do not attempt the entire
platform in one change.

Use .NET 10 LTS and a native WPF UI with Windows 11 Fluent
styling. Keep the execution engine independent of the UI.

Project assets must be portable files under api-wrangler/.
Use the installed Git executable and existing local Git
authentication. Preserve unrelated repository changes.

Scripts are trusted code with full local access, but opening,
cloning, fetching, or importing a project must not execute them.

Before implementing later modules, complete the current phase's
exit criteria. Maintain implementation status, requirement
traceability, compatibility notes, and architecture decisions.

Deliver working vertical slices with tests and a concise report.
Do not use production stubs that report success.
```

## 35. Initial architecture decisions and revision policy

| Decision | Baseline |
|---|---|
| Desktop framework | WPF with Fluent styling |
| Runtime | .NET 10 LTS, current supported servicing release |
| Canonical project format | Versioned JSON plus CSV/JSON fixtures |
| Shared storage | Git repository under `api-wrangler/` |
| Git implementation | Local Git executable |
| Git authentication | Existing Git helper/SSH configuration |
| Local operational storage | SQLite and bounded artifact files |
| JavaScript | Trusted Node worker |
| Secrets | Host-local protected storage and references |
| Hosted replacement | Self-hosted control plane, scheduler, and runners |
| Public webhooks | Optional team-operated gateway |
| High-volume results | Artifact storage, not automatic Git commits |
| Team synchronization | Explicit Git operations |
| AI | Optional bring-your-own-provider integration |
| Licensing limits | None imposed by product design |

Architecture changes require an ADR explaining the problem, alternatives, consequences, and migration impact.

Performance failures should trigger profiling and targeted changes before replacing the UI framework.

## 36. Reference material

These references inform implementation choices; this specification remains the product contract.

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [WPF styles and Fluent theme](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/styles-templates-overview)
- [Git credential handling](https://git-scm.com/docs/gitcredentials)
- [Git credential interface](https://git-scm.com/docs/git-credential)
- [Postman current pricing and capability matrix](https://www.postman.com/pricing/)
- [Postman Collection Runner](https://learning.postman.com/docs/tests-and-scripts/running-collections/intro-to-collection-runs)
- [Postman datasets](https://learning.postman.com/docs/tests-and-scripts/datasets/use-datasets)
- [Postman Application Inventory](https://learning.postman.com/latest-v-12/docs/tests-and-scripts/test-applications/overview)

**End of specification.**