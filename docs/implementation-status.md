# Implementation status

## Phase 12.1 complete: Playwright package, traffic matching, and combined results (2026-11-09 01:05 IST)

Phase 12.1 is implemented and verified on branch `phase12-playwright-traffic`:
- P12.1 (Issue #47):
  - Domain models (`PlaywrightModels.cs`): `CapturedTrafficRecord`, `TrafficMatchResult`, `TrafficAssertionRule`, `CapturedTrafficValidationResult`, `DependencyEdge`, `TrafficDependencyMap`, `PlaywrightCombinedScenarioResult`, and `PlaywrightSummaryReport`.
  - TypeScript integration (`workers/node/src/playwright.ts`):
    - `PlaywrightTrafficCollector`: Listens to `page.on('request')` and `page.on('response')`, calculates request durations, redacts credentials and sensitive JSON values, bounds body payloads (default 256 KiB), flags service workers, cached responses, and WebSocket upgrades, and writes capture files.
    - `attachApiWranglerCollector`: Helper for one-line attachment to Playwright pages.
  - Application services (`PlaywrightTrafficService.cs`):
    - `RedactTraffic`: Redacts headers (`Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`, etc.) and sensitive JSON keys (`password`, `token`, `secret`, `apiKey`), truncating payloads exceeding size limits.
    - `MatchTraffic`: Specificity scoring for origins, methods, exact/parameterized/wildcard path templates, and headers, with deterministic precedence and ambiguity detection.
    - `ValidateTrafficRecord`: Evaluates response status codes, headers, and body JSON properties (`Equals`, `LessThan`, `GreaterThan`, `Contains`, `Regex`).
    - `EvaluateScenario`: Unifies UI and API assertion outcomes. When a UI scenario passes but an API assertion fails, marks combined outcome as `Failed`, designates failure source as `API`, and explicitly names the failing browser scenario. Discloses encountered browser limitations (`BrowserHttpCache`, `ServiceWorkerInterception`, `WebSocketTraffic`, `BoundedPayloadLimit`).
    - `BuildDependencyMap`: Generates sequential API dependency graph and call frequency maps.
    - `GenerateDraftRequests`: Bootstraps draft `HttpRequestDefinition` assets from unmatched traffic.
    - `GenerateCiArtifacts`: Emits machine-readable `playwright-api-report.json` and formatted `playwright-api-summary.md` with scenario breakdown tables.
  - CLI operations: Added `playwright evaluate` command to `api-wrangler` CLI (`Program.cs`) for headless CI evaluation of captured test traffic.
  - Integration tests (`PlaywrightTrafficTests.cs`): 4 tests verifying passing UI with failing API assertion naming the scenario, ambiguous/unmatched traffic surfacing and secret redaction, explicit limitation disclosure and CI artifact generation, and dependency mapping/draft request generation.
  - Full automated test suite: 288 passed, 0 failed, 0 skipped. `scripts/Test-Desktop.ps1` passed cleanly. `dotnet format --verify-no-changes` passed.
  - Documentation: Verified in `docs/phase-12.1-progress.md`.

## Phase 11 complete: Distributed load and hosted mocks (2026-11-09 00:52 IST)

Phase 11 is fully implemented and certified complete on branch `phase11-hosted-mocks`:
- P11.2 (Issue #45):
  - Domain models (`MockModels.cs`): Enhanced `MockServerConfiguration` (`AuthToken`, `AdminApiKey`, `RequireAuthentication`, `MaxLogRetention`) and added `HostedMockDeploymentStatus`.
  - Application services (`MockEngine.cs`):
    - Mandatory authentication for remote exposure: `AllowExternalBinding` requires `AuthToken` or startup is aborted with `InvalidOperationException`.
    - Token authentication: Constant-time validation (`CryptographicOperations.FixedTimeEquals`) supporting both `Authorization: Bearer <token>` and `X-Api-Key: <token>`, returning 401 Unauthorized on invalid/missing credentials.
    - Administrative control plane: `GET /__admin/health` (public readiness probe), `GET /__admin/rules`, `GET /__admin/logs`, and `POST /__admin/state/reset` for test isolation.
    - Bounded log retention: In-memory logs capped at `MaxLogRetention` using FIFO eviction to prevent memory leakage in long-running services.
    - Semantic equivalence: Local loopback and remote hosted mock deployments execute identical rules, deterministic precedence, scenario state transitions, and fault simulations.
  - CLI operations: Added `mock serve` command to `api-wrangler` CLI (`Program.cs`) for headless mock hosting in Docker/Linux/Windows containers.
  - Operational documentation: Published `docs/operations/hosted-mock-deployment.md`.
  - Integration tests (`HostedMockTests.cs`): 5 tests verifying mandatory authentication on external binding, unauthorized request rejection, semantic equivalence between local and hosted mocks, administrative endpoints and state reset, and bounded log retention.
- P11.1 (Issue #44):
  - Domain models (`DistributedLoadModels.cs`): `DistributedWorkerAssignment`, `DistributedLoadPlan`, `DistributedWorkerReport`, and `DistributedLoadSummary`.
  - Application services (`DistributedLoadCoordinator.cs`):
    - `PlanDistribution`: Multi-worker partitioning of virtual users, target arrival rate (RPS), and contiguous input slice boundaries (`InputSliceStartIndex`, `InputSliceCount`), with coordinated synchronized start timestamps.
    - `AggregateResults`: Strict snapshot hash matching (rejects unaligned or mismatched worker snapshots), truthful multi-worker histogram pooling via `LoadExecutionEngine.MergeHistograms` to prevent percentile averaging corruption, missing worker loss tracking (`PartialWorkerLoss`, `MissingWorkerIds`), unskewed achieved throughput reporting, error categorization pooling, and multi-worker threshold evaluation.
  - Integration tests (`DistributedLoadTests.cs`): 5 tests verifying even input slice and VU allocation, rejection of mismatched worker snapshots, truthful histogram aggregation without averaging, worker loss visibility, and multi-worker threshold evaluation.
- Full automated test suite: 283 passed, 0 failed, 0 skipped. `scripts/Test-Desktop.ps1` passed cleanly. `dotnet format --verify-no-changes` passed.
- Documentation: Verified in `docs/phase-11.1-progress.md`, `docs/phase-11.2-progress.md`, and `docs/phase-11-exit-review.md`.

## Phase 10 complete: Unattended execution and monitoring (2026-11-09 00:30 IST)

Phase 10 is fully implemented and certified complete on branch `phase10-scheduler-notifications`:
- P10.3 (Issue #42):
  - Domain models (`SchedulerModels.cs`): `ScheduleType`, `ScheduleOverlapPolicy`, `ScheduleMissedRunPolicy`, `NotificationSeverity`, `NotificationRoute`, `ScheduleDefinition`, `ScheduleExecutionRecord`, `NotificationEvent`, and `NotificationDeliveryDiagnostic`.
  - Application services (`SchedulerService.cs`):
    - `SchedulerEngine`: Designated deployment ownership (`DesignatedDeploymentId`) ensuring opening the repository on multiple laptops never duplicates scheduled monitoring; IANA time zone conversions and DST handling; missed run interval suppression (`SkipMissedIntervals`) preventing downtime backlog flooding; overlap prevention (`Skip`) avoiding overlapping executions; and pinned approved commit dispatch (`ApprovedCommitSha`).
    - `NotificationDispatcher`: Generic HTTP notification webhooks, host-local secrets resolution (`SecretEnvVarName` via environment variables) without Git credential exposure, failure alert deduplication and cooldown suppression, and recovery notifications after failures.
  - Integration tests (`SchedulerServiceTests.cs`): 5 tests verifying multi-client designated deployment ownership, downtime missed interval suppression, overlap skipping, alert cooldown deduplication, and authenticated recovery webhooks.
  - Full automated test suite: 273 passed, 0 failed, 0 skipped. `scripts/Test-Desktop.ps1` passed cleanly (`{"editorRoundTrip":true,"httpRoundTrip":true,"cancellation":true,"dispatcherTicksDuringRequest":9}`). `dotnet format --verify-no-changes` passed.
  - Documentation: Verified in `docs/phase-10.3-progress.md` and `docs/phase-10-exit-review.md`.

## Phase 10.2 complete: Control plane, fenced leases, artifacts, and crash recovery (2026-11-09 00:20 IST)

Phase 10.2 is implemented and verified on branch `phase10-control-plane-leases`:
- P10.2 (Issue #41):
  - Domain models (`ControlPlaneModels.cs`): `ControlPlaneJobQueueItem`, `ArtifactBackendType`, `ArtifactMetadata`, `ArtifactPolicyConfig`, and `ControlPlaneHealthReport`.
  - Application services (`ControlPlaneService.cs`):
    - `ControlPlaneQueue`: Outbound polling job queue, capability-matching lease acquisition with strictly monotonic `FencingToken`, fenced heartbeat extensions and completions rejecting stale or superseded tokens, cancellation propagation, and crash/lease expiration recovery.
    - Crash recovery policy: Lost potentially mutating jobs become `Indeterminate` and are not silently retried; lost non-mutating jobs are safely re-enqueued within attempt budgets.
    - `ArtifactStorageService`: Streamed artifact storage with SHA-256 calculation, size policy limits (`MaxSizeBytes`), operational artifact retention sweeps (`ApplyRetentionSweep`), and system health reporting (`GetHealthReport`).
  - Integration tests (`ControlPlaneTests.cs`): 6 tests verifying fenced lease dispatch and completion, lost mutating job indeterminate status, non-mutating retries, cancellation signaling, artifact size limits/checksums, and retention sweeps/health reporting.
  - Full automated test suite: 268 passed, 0 failed, 0 skipped. `scripts/Test-Desktop.ps1` passed cleanly (`{"editorRoundTrip":true,"httpRoundTrip":true,"cancellation":true,"dispatcherTicksDuringRequest":8}`). `dotnet format --verify-no-changes` passed.
  - Documentation: Verified in `docs/phase-10.2-progress.md`.

## Phase 10.1 complete: Runner service, enrollment, and exact-snapshot execution (2026-11-09 00:09 IST)

Phase 10.1 is implemented and verified on branch `phase10-runner-enrollment`:
- P10.1 (Issue #40):
  - Domain models (`RunnerModels.cs`): `RunnerPlatform`, `RunnerStatus`, `RunnerEnrollmentScope`, `RunnerJobOutcome`, `RunnerDescriptor`, `RunnerEnrollmentToken`, `RunnerJobDefinition`, and `RunnerJobResult`.
  - Application services (`RunnerService.cs`):
    - `RunnerEnrollmentService`: Cryptographic token generation (`awr_tok_...`), SHA-256 hashed secret storage, constant-time equality validation (`CryptographicOperations.FixedTimeEquals`), role-based scope enforcement (Admin inheritance), expiration, revocation, and atomic rotation.
    - `ExactCommitJobExecutor`: Enforces exact commit execution (prevents running unverified branch tips), isolates execution directories, writes execution manifest with host machine and dedicated account, enforces host-local secrets (`API_WRANGLER_SECRET_*`) without desktop credentials, and reports crashes during mutating runs as `Indeterminate` with descriptive diagnostics.
    - Capability label matching (`MatchCapabilities`) supporting multi-label requirements.
  - CLI operations: Added `runner enroll` and `runner exec` commands to `api-wrangler` CLI (`Program.cs`) for unattended execution without desktop installation.
  - Integration tests (`RunnerServiceTests.cs`): 5 tests verifying token creation/validation, scope permissions, expiration/revocation/rotation, exact commit enforcement and execution manifests, indeterminate crash outcomes, capability matching, and host secret isolation.
  - Full automated test suite: 262 passed, 0 failed, 0 skipped. `scripts/Test-Desktop.ps1` passed cleanly (`{"editorRoundTrip":true,"httpRoundTrip":true,"cancellation":true,"dispatcherTicksDuringRequest":8}`). `dotnet format --verify-no-changes` passed.
  - Documentation: Verified in `docs/phase-10.1-progress.md`.

## Phase 9 complete: Local performance testing (2026-11-08 23:58 IST)

Phase 9 is fully implemented and certified complete on branch `phase9-histograms-reports`:
- P9.2 (Issue #38):
  - Domain models (`LoadTestModels.cs`): added `LoadThresholdPolicy` (`MaxP95LatencyMs`, `MaxP99LatencyMs`, `MaxErrorRatePercent`, `MinThroughputRps`) and `LoadThresholdEvaluation` (`Passed`, `Violations`).
  - Application services (`LoadExecutionEngine.cs`):
    - True histogram percentile calculations (min, mean, max, p50, p90, p95, p99) derived directly from sorted measurement pools.
    - Multi-worker histogram merging (`MergeHistograms`) pooling sample sets and recalculating percentiles across the whole distribution.
    - Monotonic duration measurement via `Stopwatch.GetTimestamp()` high-resolution counters.
    - Threshold evaluation (`EvaluateThresholds`) reporting distinct violations.
    - Distinct categorization of functional HTTP status codes (e.g. `HTTP 500`), transport/network exceptions (e.g. `HttpRequestException`, connection termination), and threshold policy failures.
  - Desktop integration: added threshold settings (`MaxP95LatencyMs`, `MaxErrorRatePercent`) in `LoadTestingViewModel` and `MainWindow.xaml`, and integrated threshold pass/fail violations into report summaries.
  - Integration tests (`LoadExecutionEngineTests.cs`): 8 tests passing, verifying histogram calculations, multi-worker histogram pooling, threshold evaluations, distinct failure categorization, target safety gates, VU execution, consecutive error aborts, and clean cancellation.
  - Full automated test suite: 257 passed, 0 failed, 0 skipped. `scripts/Test-Desktop.ps1` passed cleanly. `dotnet format --verify-no-changes` passed.
  - Documentation: Verified in `docs/phase-9.2-progress.md` and `docs/phase-9-exit-review.md`.

## Phase 9.1 complete: Closed-loop and open-loop load execution (2026-11-08 23:34 IST)

Phase 9.1 is implemented and verified on branch `phase9-load-execution`:
- P9.1 (Issue #37):
  - Domain models (`LoadTestModels.cs`): `LoadModelType`, `LoadProfileType`, `LoadTestConfiguration`, `LoadIterationMetric`, `LatencyHistogram`, and `LoadTestReport`.
  - Application services (`LoadExecutionEngine.cs`):
    - `LoadExecutionEngine`: High-performance execution engine supporting both closed-loop virtual user models and open-loop arrival rates.
    - Safety controls: enforces target confirmation (`RequireTargetConfirmation`), stops automatically on error thresholds (`MaxConsecutiveErrors`), and guarantees clean cancellation without leaking worker tasks.
    - Telemetry and metrics: computes throughput (achieved vs target RPS), detects load generator saturation, and calculates complete latency histograms (Min, Mean, P50, P90, P95, P99, Max).
  - Desktop integration: added `LoadTestingViewModel` and a dedicated "Load testing" tab in `MainWindow.xaml` for running tests with real-time metric summaries.
  - Integration tests (`LoadExecutionEngineTests.cs`): 5 tests verifying percentile calculation, safety confirmation gate, VU execution against local mock server, consecutive error threshold aborts, and clean cancellation.
  - Automated test suite: 254 passed, 0 failed, 0 skipped. `scripts/Test-Desktop.ps1` passed cleanly. `dotnet format --verify-no-changes` passed.
  - Documentation: Verified in `docs/phase-9.1-progress.md`.

## Phase 8 complete: Mocks and webhook debugging (2026-11-08 23:22 IST)

Phase 8 is fully implemented and verified on branch `phase8-webhook-debugging`:
- P8.2 (Issue #35):
  - Domain models (`WebhookModels.cs`): `WebhookListenerDefinition`, `WebhookSignatureType`, `WebhookCapturedEvent`, `WebhookReplayRequest`, and `WebhookReplayResult`.
  - Application services (`WebhookService.cs`):
    - `WebhookService`: In-process loopback ASP.NET Core listener.
    - Byte-perfect cryptographic signature verification (`HmacSha256Hex`, `HmacSha256Base64`, `HmacSha1Hex`) using `CryptographicOperations.FixedTimeEquals` directly on raw bytes prior to any JSON/text parsing.
    - Safeguards: body size limits (`MaxBodySizeBytes`, HTTP 413) and destination policy enforcement (`AllowedForwardingDestinations`, HTTP 403) to prevent SSRF and unrestricted open forwarding.
    - Replay engine: original byte-preserving and edit-and-replay dispatching with audit records.
  - Desktop integration: added "Webhook Debugger" tab under "Mocks & Webhooks" in `MainWindow.xaml` to inspect incoming payloads, signature verification results, and replay history.
  - Integration tests (`WebhookServiceTests.cs`): 3 tests covering signature verification against raw bytes and failure on tampering, webhook capture with headers, and destination policy replay enforcement.
  - Verification: 249 tests passed, 0 failed, 0 skipped. `scripts/Test-Desktop.ps1` passed cleanly. `dotnet format --verify-no-changes` passed.
  - Documentation: Verified in `docs/phase-8.2-progress.md` and `docs/phase-8-exit-review.md`.

## Phase 8.1 complete: Local mock engine, deterministic matching, and fault scenarios (2026-11-08 23:12 IST)

Phase 8.1 is implemented and verified on branch `phase8-local-mock-engine`:
- P8.1 (Issue #34):
  - Domain models (`MockModels.cs`): `MockRule`, `MockFaultType`, `MockServerLogEntry`, `MockServerConfiguration`, and `MockMatchResult`.
  - Application services (`MockEngine.cs`, `MockExampleGenerator.cs`):
    - `MockEngine`: In-process loopback ASP.NET Core server (`127.0.0.1` enforced unless explicitly opted into external binding).
    - Deterministic matching: specificity scoring combining rule priority, exact vs parameterized vs wildcard paths, query parameter checks, header checks, and body regex matching. Ambiguity detection and detailed match explanations.
    - Stateful scenarios: state machine prerequisites (`ScenarioStateRequired`) and transitions (`ScenarioStateTransition`), with manual and automated state reset.
    - Fault simulations: delays, HTTP status codes, malformed payloads, rate-limiting (429 with `Retry-After`), connection abortions (`context.Abort()`), and seeded intermittent failure rates.
    - Example generator: generates mock rules from OpenAPI schemas and saved responses.
  - Desktop integration: added `MockServerViewModel` and "Mocks & Webhooks" tab in `MainWindow.xaml` featuring rule configuration, start/stop controls, and real-time request audit logs.
  - Integration tests (`MockEngineTests.cs`): 5 tests covering matching specificity, scenario transitions, HTTP server hosting, fault simulations, and loopback binding enforcement.
  - Automated test suite: 246 passed, 0 failed, 0 skipped. `scripts/Test-Desktop.ps1` passed cleanly. `dotnet format --verify-no-changes` passed.
  - Documentation: Verified in `docs/phase-8.1-progress.md`.

## Phase 7 complete: GraphQL, gRPC, and streaming (2026-11-08 23:00 IST)

Phase 7 is fully implemented and verified on branch `phase7-graphql-highlighting`:
- P7.3 (Issue #56):
  - GraphQL Formatting Engine (`GraphQLService.cs`):
    - `FormatDocument`: parses operations, selections, arguments, block strings, and comments, applying uniform indentation and brace structuring. Non-destructive; returns actionable diagnostics on invalid syntax / mismatched braces without mutating document text.
  - AvalonEdit Highlighting (`DatasetHighlightingDefinitions.cs`):
    - Added `GraphQL` syntax definition (.graphql, .gql) highlighting operations (`query`, `mutation`, `subscription`, `fragment`), SDL types (`type`, `interface`, `union`, `enum`, `scalar`, `input`), scalars (`Int`, `Float`, `String`, `Boolean`, `ID`), variables (`$var`), directives (`@dir`), block strings (`"""`), and punctuation.
  - Desktop integration: converted query/variables/response editors in "Protocols & Streaming" tab to AvalonEdit with syntax highlighting and added "Format" action button.
  - Integration tests (`GraphQLAndGrpcTests.cs`): added tests verifying document formatting indentation and mismatched brace diagnostic preservation.
  - Verification: 241 tests passed, 0 failed, 0 skipped. `scripts/Test-Desktop.ps1` passed cleanly. `dotnet format --verify-no-changes` passed.
  - Documentation: Verified in `docs/phase-7.3-progress.md` and `docs/phase-7-exit-review.md`.

## Phase 7.2 complete: WebSocket and SSE sessions and bounded transcripts (2026-11-08 22:45 IST)

Phase 7.2 is implemented and verified on branch `phase7-websocket-sse`:
- P7.2 (Issue #32):
  - Domain models (`StreamingModels.cs`): `StreamingProtocol`, `StreamDirection`, `StreamEventType`, `StreamingTranscriptEntry`, `StreamingReconnectionPolicy`, `StreamingLimits`, and `SseEventEntry`.
  - Application services (`StreamingService.cs`):
    - `WebSocketSession`: full async connection, custom HTTP headers, subprotocols, text frame transmission, sliding bounded message buffer with `DroppedCount` telemetry, predicate-driven `AwaitMessageAsync` with timeout/cancellation, and graceful disconnect handshake.
    - `SseSession`: W3C EventSource line parsing (`event:`, multi-line `data:`, `id:` tracking with `Last-Event-ID` header injection, `retry:`, and comment filtering), sliding bounded buffer with `DroppedCount`, predicate-driven `AwaitEventAsync`, and graceful cancellation.
  - Desktop integration: Upgraded `ProtocolExpansionViewModel` and added WebSocket & SSE streaming controls and live chronological transcript pane to the "Protocols & Streaming" tab in `MainWindow.xaml`.
  - Integration tests (`WebSocketAndSseTests.cs`): In-process ASP.NET Core test server hosting WebSocket echo and SSE stream endpoints. Tests connection, header/event parsing, bounded FIFO retention drops, predicate awaiting, and clean disconnection.
  - Automated test suite: 239 passed, 0 failed, 0 skipped. Desktop smoke (`scripts/Test-Desktop.ps1`) passed cleanly. Format verification passed (`dotnet format --verify-no-changes`).
  - Documentation: Verified in `docs/phase-7.2-progress.md`.

## Phase 7.1 complete: GraphQL and gRPC editors, adapters, and contracts (2026-11-08 22:30 IST)

Phase 7.1 is implemented and verified on branch `phase7-graphql-grpc`:
- P7.1 (Issue #31):
  - `GraphQLService`: GraphQL SDL parser extracting operations, types, fields, arguments, and scalars. Introspection query generator and parser into `GraphQLSchemaDefinition`. Schema-aware field completion suggestions. Query validation against root types. HTTP POST execution with `{ query, variables, operationName }`, error extraction (`message`, `path`, `locations`, `extensions.code`). Local fixture execution verified.
  - `GrpcService` & `ProtoParser`: Proto3 parser extracting packages, services, RPC methods, and call patterns (Unary, ServerStreaming, ClientStreaming, BidirectionalStreaming). Recursive proto import resolution across include directories with actionable diagnostics for missing include paths.
  - `GrpcFrameCodec`: Standard 5-byte gRPC framing (compression flag + 4-byte big-endian length) encoding and decoding.
  - `GrpcStreamingSession`: Explicit protocol-specific chronological transcript tracking (`Connected`, `MetadataSent`, `MessageSent`, `MessageReceived`, `TrailersReceived`, `Completed`, `Cancelled`), preserving full status codes (`grpc-status`), details (`grpc-message`), and trailers. Deadlines and cancellation support.
  - Desktop integration: Added `ProtocolExpansionViewModel` and "GraphQL & gRPC" tab in `MainWindow.xaml`.
  - Documentation: Verified in `docs/phase-7.1-progress.md`.
- Automated test suite: 235 passed, 0 failed, 0 skipped (`GraphQLAndGrpcTests`, `ProjectImportExportTests`, and all Phase 0-6 tests). Desktop smoke (`scripts/Test-Desktop.ps1`) passed cleanly. Solution builds cleanly in Release configuration with zero warnings/errors. Format verification passed (`dotnet format --verify-no-changes`).

## Phase 6.3 complete: Transactional Postman/OpenAPI/cURL/HAR import and supported exports (2026-11-08 22:18 IST)

Phase 6.3 is implemented and verified on branch `phase6-import-export`:
- P6.3 (Issue #29):
  - `CurlParser`: string tokenizer parsing methods, URLs, headers, bodies (raw & form-data), basic auth, redirect/TLS options without invoking any shell or subprocess.
  - `PostmanImporter`: imports Postman Collection v2.1 and Environments, recurses folder hierarchies, maps query/headers/auth/bodies/scripts, detects sensitive variables for `.local/` secret extraction guidance, and flags unsupported Postman features.
  - `PostmanExporter`: exports API Wrangler collections and environments to compatible Postman v2.1 format, with secret redaction and export reporting.
  - `OpenApiImporter`: parses OpenAPI 3.0/3.1 specs, groups endpoints into folders by tags, creates requests with query/header/schema parameters, extracts server URLs into baseUrl collection variable, and generates entity counts.
  - `HarImporter`: parses HTTP Archive 1.2 entries into collection requests with methods, URLs, headers, query params, and postData.
  - `ProjectImportService`: 10-step transactional import preview, isolated staging, schema/reference validation via `AssetValidator`, atomic publication via `IProjectSession.SaveAsync`, and saving `reports/import-report-<id>.json`. Guarantees failed imports never partially overwrite or corrupt valid project assets.
  - Desktop integration: Added `ProjectImportViewModel` and "Import & export" tab in `MainWindow.xaml`.
  - Documentation: Verified in `docs/phase-6.3-progress.md`.
- Automated test suite: 227 passed, 0 failed, 0 skipped (`ProjectImportExportTests`, `PostmanCompatibilityAdapterTests`, `NodeScriptHostTests`, and all Phase 0-5 tests). Desktop smoke (`scripts/Test-Desktop.ps1`) passed cleanly. Solution builds cleanly in Release configuration with zero warnings/errors. Format verification passed (`dotnet format --verify-no-changes`).

## Phase 6.2 complete: Postman compatibility adapter and fixtures (2026-11-08 21:58 IST)

Phase 6.2 is implemented and verified on branch `phase6-postman-adapter`:
- P6.2 (Issue #28):
  - Postman Runtime Adapter (`workers/node/src/worker.ts`):
    - `pm.test` & `pm.expect` with Chai BDD matchers (`to.equal`, `to.eql`, `to.be.ok`, `to.have.property`, `to.include`, `to.be.above`, `to.be.at.least`, `to.be.below`, `to.be.at.most`, `to.be.a`, `to.be.an`, `to.be.true`, `to.be.false`, `to.be.null`, `to.be.undefined`, `to.match`, and `.not` inversion).
    - `pm.response`: code, status, headers, responseTime, size, text(), json(), and assertions `to.have.status()`, `to.have.header()`, `to.have.jsonBody()`, `to.be.ok`, `to.be.clientError`, `to.be.serverError`.
    - `pm.request`: method, url (`toString()`, `getPath()`), headers, body.
    - Variable scopes: `pm.environment`, `pm.collectionVariables`, `pm.variables`, `pm.iterationData` with `get`, `set`, `has`, `unset`, `clear`, `toObject`.
    - Secondary requests: `pm.sendRequest` with callback or Promise support.
    - Flow control: `postman.setNextRequest(target)`.
    - Legacy syntax: `tests["name"] = boolean` and legacy globals (`responseCode`, `responseBody`, `responseTime`).
    - Visible traps: `pm.visualizer`, `pm.cookies`, `pm.execution.skipRequest`.
  - Static Analysis (`PostmanCompatibilityService` in `ApiWrangler.Application`):
    - AST/regex script analyzer identifying supported and unsupported Postman features.
  - Automated tests: 218 passed, 0 failed, 0 skipped (`PostmanCompatibilityAdapterTests`, `NodeScriptHostTests`, and all Phase 0-5 tests).
  - Desktop smoke (`scripts/Test-Desktop.ps1`): passed (`{"editorRoundTrip":true,"httpRoundTrip":true,"cancellation":true,"dispatcherTicksDuringRequest":9}`).
  - Formatter (`dotnet format --verify-no-changes`): passed with zero deviations.
  - Documentation: Verified in `docs/phase-6.2-progress.md`.

## Phase 6.1 complete: Production Node worker, trust policy, and packages (2026-11-08 21:46 IST)

Phase 6.1 is implemented and verified on branch `phase6-scripting-and-packages`:
- P6.1 (Issue #27):
  - `ProjectTrustService`: Host-local trust storage under local app data, non-execution on repository open/clone/fetch/import, deterministic SHA-256 script hashing across all project scripts and manifests, detection of tampered scripts, and explicit untrusted execution blocking.
  - `NodeRuntimeService`: Node and npm discovery across PATH, standard directories, and environment variables, with Node LTS 20+ validation and dependency diagnostics.
  - `NpmPackageService`: Package metadata extraction without code execution, lockfile presence verification, `npm ci --ignore-scripts` enforcement, separate explicit lifecycle execution, and host configuration for private registries.
  - Production Node Worker (`workers/node/src/worker.ts` & `NodeScriptHost`): Framed binary IPC, native `wrangler.test` and `wrangler.expect` assertions, request/response contexts, scoped variable mutations, bounded console logs, and hard termination of infinite loops/descendant processes via wall-clock timeouts.
  - Documentation: Verified in `docs/phase-6.1-progress.md`.
- Automated test suite: 213 passed, 0 failed, 0 skipped. Desktop smoke (`scripts/Test-Desktop.ps1`) passed cleanly. Solution builds cleanly in Release configuration with zero warnings/errors. Format verification passed (`dotnet format --verify-no-changes`).

## Phase 5 complete: Git collaboration, reviewed commits, and semantic conflict resolution (2026-11-08 20:48 IST)

Phase 5 is complete, fully verified, and merged into `main` via PR #72 (commit `74e0719`). Milestone A (Team HTTP regression) is accepted. All Phase 5 sub-issues (#23, #24, #25) and parent Epic #22 are closed and marked `Done` in GitHub Project 4.
- P5.1: `GitRepositoryService` with branch creation/switching, detached HEAD, worktree rejection, clean status facts, fetch without changing working files, fast-forward, explicit merge/rebase, and scoped restore.
- P5.2: `GitCommitReviewService` with scoped `api-wrangler/` file staging, complete cached diff review up to 4 Mi characters, high-confidence secret scanning, commit message review, and explicit push.
- P5.3: `GitConflictResolutionService`, `ProjectProposalValidator`, and `GitConflictWorkbenchViewModel`. Three-way semantic JSON proposal engine (independent object/ID-keyed row edits, ordered headers, preserving unknown fields, unilateral reordering, reporting same-field, delete/edit, duplicate-ID, and incompatible reorder conflicts). Bounded final diff review, explicit choices (Edited, Local, Incoming, Delete, Save Both), schema and reference validation before staging, recovery snapshots, and staging without losing unrelated work.
- Two-clone acceptance: verified across two independent clones with simultaneous conflicting JSON edits, resolution, commit, push, and fast-forward pull.
- Verification: 188 automated tests passed (0 failed, 4 skipped). Desktop smoke passed (`scripts/Test-Desktop.ps1`). Desktop Git smoke passed (`--git-smoke artifacts/git-smoke`). Format verification passed (`dotnet format --verify-no-changes`). Solution builds cleanly in Release configuration with zero warnings/errors. All hosted CI checks on PR #72 passed (`desktop`, `headless (ubuntu-latest)`, `headless (windows-latest)`).

## Phase 5 parallel Git work checkpoint (2026-11-08 19:32 IST)

- P5.1 follow-up: fixed case-insensitive restore safety on Windows, added visible labels and a GitHub compare action (including Git URL rewrite handling), and extended local workflow coverage.
- P5.2 includes high-confidence secret scanning, explicit scoped staging, strict JSON syntax checks, desktop project schema/reference validation, exact index-tree and branch/HEAD confirmation, explicit commit, and separate push. Cached diff review returns complete output up to 4 Mi characters and fails closed above that limit.
- P5.3 started with a read-only JSON three-way proposal engine. It merges independent object and ID-keyed row edits, preserves unknown fields and unilateral reorder, and reports same-field, delete/edit, duplicate-ID, and incompatible reorder conflicts.
- Verification: prior Release solution build and desktop Git smoke passed; the latest focused Release test batch passed 15/15 with 0 skipped, covering P5.2 review/scanning and P5.3 proposals.
- P5.1 remains In Progress for provider-specific rejected-auth and manual accessibility review. P5.2 remains In Progress with high-confidence-only scanner patterns. P5.3 remains In Progress; desktop/Git conflict integration, other file types, and two-clone acceptance remain. See the phase progress notes and Restart bookmark.
## Phase 5.1 Git workflow checkpoint (2026-11-08 17:58 IST)

P5.1 has started on `phase5-repository-workflows`:
- Added `GitRepositoryService` for Git executable discovery/version, repository discovery/init/clone, status facts (dirty paths, branch/upstream, independent ahead/behind, remotes, detached HEAD, linked worktree, write access, merge/rebase/sequencer state), branch listing/history, remote add/select, fetch/push, branch creation/switch, fast-forward, explicit merge/rebase, and scoped `api-wrangler/` asset restore.
- Added a desktop Git collaboration workbench exposing repository open/status, clone/init, remote operations, branch operations, integration, history, and asset restore. Git runs through argument arrays and configured Git authentication; the UI does not embed credentials. Branch/integration operations require a clean whole working tree; there is no automatic stash, force push, or hard reset.
- Added `scripts/Test-GitDesktop.ps1` and a desktop smoke flow. It passed clone/init/open/status, remote selection, fetch, branch create/switch/push, history, and scoped restore against a local bare remote; the screenshot was inspected.
- Existing `ProjectSession` guards continue to suspend writes during index locks and merge/rebase markers; snapshots compare Git identity across capture. No new cache was added.
- Verification: Release solution build succeeded (0 warnings/errors); four focused tests passed (0 failed/skipped), covering offline/rejected operations, real merge/rebase conflicts and recovery, external branch changes with an open project session, snapshots, index-lock write suspension, fetch immutability, dirty/unrelated files, fast-forward, branch/history/restore, detached HEAD, and linked worktrees. Desktop Git workflow smoke passed; screenshot inspected.

P5.1 remains In progress. Remaining: manually review provider-specific rejected-auth behavior and accessibility/credential prompts. A local HTTP 401 fixture hung in Git's credential path and was removed rather than treated as passing evidence. P5.2 scoped reviewed commits and P5.3 semantic conflict handling are tracked in the current Phase 5 progress checkpoint above. See `docs/phase-5.1-progress.md` and the Restart bookmark in the product spec.

## Phase 4 color-highlighted preview checkpoint (2026-11-08 11:51 IST)

P4.3 implemented and verified on branch `phase4-preview-highlighting`:
- `DatasetHighlightingDefinitions`: Registers syntax highlighting definitions for CSV (distinguishing delimiters, quoted fields, numbers), JSON, and JSON Lines in AvalonEdit's `HighlightingManager`.
- `DatasetPreviewFormatting`: Dynamically maps dataset formats ("CSV", "TSV", "JSON", "JSONL") to syntax highlighting modes.
- Desktop Integration: Upgrades dataset import preview to AvalonEdit's `TextEditor` with live syntax highlighting and two-way typography bindings (`EditorFontFamily`, `EditorFontSize`).
- Verification: 151 automated tests passed (0 failed, 4 skipped). Desktop smoke passed (`scripts/Test-Desktop.ps1`). Solution builds cleanly in Release configuration with zero warnings/errors. Format verification passed (`dotnet format --verify-no-changes`).

## Phase 4 editable grid and clipboard workflows checkpoint (2026-11-08 11:45 IST)

P4.2 implemented and verified on branch `phase4-editable-grid`:
- `EditableDatasetGrid`: Virtualized 100,000-row editable dataset grid with responsive row retrieval and cell editing (<10ms), multi-cell editing, Undo/Redo transaction history stack, Fill Down across selected rows, Duplicate Case with generated unique `_caseId`, Insert/Delete row operations, Search/Filter, and View Sorting (Ascending/Descending) without mutating underlying source dataset ordering.
- `ClipboardFormatDetector`: Recognizes spreadsheet TSV (Excel, Google Sheets), CSV text, JSON arrays, JSON Lines, raw HTTP requests, plain text, and cURL commands (enforcing the specification rule that pasted shell commands are never executed).
- `SpreadsheetSafeExport`: Provides spreadsheet-safe CSV export that neutralizes formula-leading cell triggers (`=`, `+`, `-`, `@`, `\t`, `\r`) by prefixing single quotes, while preserving native values when disabled.
- `ExternalResultAttachmentService`: Attaches pasted external test run records (JUnit XML, Newman / Postman JSON), records provenance (tool, import timestamp, SHA-256 content hash, test mapping, status), and guarantees that imported results are never represented as API Wrangler executions (`IsWranglerExecution = false`).
- Desktop Workbench: Adds interactive "Dataset grid & clipboard" tab with search/filter, undo/redo, paste, fill-down, duplicate case, insert, delete, spreadsheet-safe CSV export, and external results attachment.
- Verification: 145 automated tests passed (0 failed, 4 skipped). Desktop smoke passed (`scripts/Test-Desktop.ps1`). Solution builds cleanly in Release configuration with zero warnings/errors. Format verification passed (`dotnet format --verify-no-changes`).

## Phase 4 file-backed import and desktop checkpoint (2026-11-08 11:24 IST)

P4.1 is ready for review in PR #69: streaming file-backed JSONL preparation/publication with hash checking, stable case identity, full-import conversions, and desktop source/encoding/mapping/preview/accept/cancellation flow. Input source changes after preview do not alter accepted content. Existing project transactions protect definitions and stale writes; immutable source files participate in project snapshots. Release solution build passed; 62 dataset/persistence tests passed without skips. Desktop dataset smoke verified explicit conversion, stale-options rejection, and exact prepared-source import; screenshot inspected. A diagnostic tab-name compile error was fixed before the successful desktop rebuild/smoke. See `docs/dataset-import.md` for limits and remaining polish. P4 stays In progress; P4.1 is not Done before review/acceptance/merge. Existing unrelated edits preserved.

## Phase 4 JSON admission checkpoint (2026-11-08 10:09 IST)

JSON-array reads enforce a configurable per-record byte budget (default 1 MiB) before deserialization using a bounded-read stream wrapper. Record boundaries respect nesting and escaped strings; UTF-8 bytes are counted, total dataset length is not constrained by the record budget, and the caller's stream remains open. Batched dataset/rollback verification passed 46 tests with no failures/skips in one invocation; no standalone build. File-backed import/publication, full-import conversion integration and UI remain; P4/P4.1 stay In progress.

## Phase 4 inline import checkpoint (2026-11-08 09:00 IST)

`DatasetImport` prepares bounded inline datasets without touching assets and returns an immutable `PreparedDatasetImport` for explicit acceptance. Existing string case IDs are preserved; duplicate/invalid IDs fail, and missing IDs require explicit addition preview. Commit reuses journaled `IProjectSession.SaveAsync` with expected-hash conflict checks and existing schema/path/writer validation. Persisted inline sources are included in project snapshots. One batched invocation (`dotnet test tests/ApiWrangler.IntegrationTests -c Release --no-restore --filter 'FullyQualifiedName~Dataset|FullyQualifiedName~Save_ReplacementFailure_RollsBackAllFiles' --verbosity minimal`) passed 43 tests, none skipped. No standalone build. P4/P4.1 remain In progress: large file-backed imports, JSON-array admission bounds, full-import conversion integration, flattening/dates, encoding selection and UI remain. Existing unrelated edits preserved.

## Phase 4 JSON and preview continuation (2026-11-08 08:57 IST)

Added streaming JSON-array/JSONL object readers preserving nested JSON, missing fields, and explicit nulls; duplicate properties are rejected recursively. JSONL supports BOM, CRLF, cancellation, and bounded records. Added a headless preview capped at 1,000 rows (100 default), column mapping, explicit culture-aware decimal/boolean/string conversions, and opt-in empty-string-to-null mapping. Source assets remain untouched. JSON-array per-record admission limits, stable case IDs, transactional import/snapshots, flattening/date conversion, and import UI/encoding selection remain in P4.1. Batched verification: delimited, JSON, and preview suites passed 31 tests (0 skipped); two initial failures were exact-exception test assertions, corrected before the single rerun. No standalone build or broad test suite. Draft PR #69; P4/P4.1 remain In progress.

## Phase 4 continuation (2026-11-07 22:46 IST)

P4 and P4.1 (#19/#20) are In progress. `DelimitedDatasetReader` streams raw CSV/TSV/custom-delimited records with quoted multiline fields, escaped quotes, BOM handling, leading-zero and empty-string preservation, duplicate/empty-header and record-width validation, cancellation, and per-record bounds. One necessary focused invocation (`dotnet test tests/ApiWrangler.IntegrationTests -c Release --no-restore --filter FullyQualifiedName~DelimitedDatasetTests --verbosity minimal`) compiled affected projects and passed 11 tests, none skipped. No separate build or broad suite run. JSON/JSONL, preview/mapping, explicit conversion/null policies, stable case IDs, and transactional import remain; no Phase 4 acceptance claim. Existing unrelated whitespace changes are preserved.

This is a working foundation increment, not completion of the full implementation plan. Neither Milestone A nor Milestone B has been reached.

## Completed behavior

- Pinned .NET 10 solution, central package versions, lockfiles, formatting rules, and Windows/Linux CI definitions.
- Native WPF/MVVM shell with AvalonEdit, HTTP method/URL/header/body editing, response inspection, and cancellation.
- Shared application validation and streaming HTTP engine with bounded previews/artifacts and distinct timeout/cancellation outcomes.
- Read-only, paged 100,000-case grid feasibility fixture.
- CLI `send`, `git-probe`, and `node-probe` commands with meaningful exit codes.
- Installed-Git status/fetch adapter, framed TypeScript/Node worker probe, and worker descendant-cancellation verification.
- Local HTTP fixture, integration tests, architecture checks, desktop smoke/measurement scripts, and architecture decisions.

## Historical Phase 0 verification on Windows x64

- Release build: no warnings or errors.
- Seventeen automated tests passed on Windows x64 and Linux x64, including all four explicitly enabled Node-dependent tests; none skipped in those runs.
- Desktop smoke: initial empty-editor two-way binding, HTTP POST round trip, cancellation, and dispatcher ticks during network wait.
- Local bare-remote fetch preserves working files and HEAD. Fetch of this repository's public HTTPS origin also succeeded using inherited Git configuration. This does not verify private-repository authentication or SSH.
- Ten warm process-start samples: p95 834 ms, dataset display p95 154 ms, maximum idle private bytes 87,449,600 (about 83 MiB), 17 realized grid rows, 256 cached dataset rows.
- Node 26.13.0 downloaded to ignored `.tools` and checked against the distributor's SHA-256 manifest; TypeScript compilation and npm audit passed.
- Self-contained Windows x64 preview ZIP rebuilt under locked restore; its published executable launched and passed diagnostics. Normal locked restore still passes after publishing.

Measurement host: Windows x64, AMD Ryzen 7 6800H, eight physical cores, about 21.7 GiB installed memory. Measurements use an empty foundation shell, not an ordinary saved project. No cold-start, index/search, ARM64, accessibility, or full NFR acceptance claim is made. Reproduce with `scripts/Measure-Desktop.ps1`; raw local evidence is in ignored `artifacts/`.

## Phase gates and remaining work

| Phase | Status / next required work |
|---|---|
| 0 | Local foundation implemented; private HTTPS/SSH credential flows, accessibility checks, and broader performance validation remain open; ARM64 is deferred |
| 1 | Merged (PR #57): project discovery, versioned schemas, canonical assets, journaled persistence, watching, recovery, snapshots, and a desktop asset editor are implemented and merged into main. Storage checks pass; desktop smoke covers clean reload, dirty-draft conflict decisions, invalid-text draft recovery, and UI Automation values. P1.1 (#8), P1.2 (#9), and P1.3 (#10) are Done. Manual accessibility review (#58) remains pending. |
| 2 | In progress: P2.1 and P2.2 implemented on `phase2-variables-and-secrets`. Request workbench with full lifecycle controls, repeated headers/query params, multipart/urlencoded/file-backed bodies, automatic decompression, cookies, explicit TLS override, cross-origin redirect auth stripping, and non-idempotent retry protection. P2.2 delivered variable resolution with documented precedence (RequestLocal > Dataset > RunOverride > LocalEnvironment > Environment > Collection > ProjectDefault), bounded nesting ($\le 10$), circular reference detection with cycle trace, non-shadowed explicit `{{secret:...}}` namespace, Windows DPAPI secret store (`LocalSecretStore`), deterministic dynamic helpers, and redaction engine across headers, logs, and execution previews. Release build, 44 integration tests, and desktop smoke pass. |
| 3 | Not started: collections, assertions, lifecycle, isolated iteration execution, reports, final CLI contracts |
| 4 | Only the read-only grid probe exists; imports, editing, type/null semantics, stable cases, and export remain |
| 5 | P5.1 remains In progress; P5.2 scoped review/commit work has started in parallel; P5.3 conflict resolution remains |
| 6 | Only framed IPC/process probe exists; script execution, trust policy, packages, imports, and compatibility remain |
| 7–9 | Not started: additional protocols, mocks, webhook gateway, local performance engine |
| 10–11 | Not started: control plane, runners, scheduler, distributed load, hosted mocks; do not start before foundation decisions settle |
| 12–13 | Not started: Playwright, visual flows, contract tooling, optional AI, signed installers, operational hardening |

The current desktop release target is Windows 11 x64. ARM64 was explicitly deferred by the user and no longer blocks current milestones. PR #57's hosted GitHub Actions passed after the Linux writer-lease fix: Windows and Linux headless jobs each passed 30 tests with zero skipped, and desktop build, format, and publish passed. The P1.4 accessibility review has started: a current Release desktop build and project smoke pass, including UI Automation names, live-region metadata, and draft/disk Value patterns. Manual keyboard-focus, Narrator, high-contrast, and 150% DPI checks remain pending because this session has no interactive desktop. See `docs/phase-1-accessibility-review.md`.

Phase 1 checks were verified on Windows x64: nested repository project discovery, newer schema inspection/edit rejection, missing-field diagnostics with file and JSON pointer, extension preservation, no-diff saves, local settings persistence, dirty-draft preservation through external changes, watcher overflow full-rescan signaling, in-process transaction rollback, idempotent interrupted multi-file recovery, preservation on recovery conflict, snapshot metadata, second-writer read-only access, external-edit conflict handling, and debounced watcher notification. Desktop project smoke verifies preserved dirty JSON draft, blocked stale save, explicit use-draft decision, automatic clean-editor reload, invalid-text recovery, and read-only second-writer/newer-schema inspection. Desktop HTTP smoke verifies binding, send, cancellation, and dispatcher responsiveness. Latest full Release build passed with zero warnings/errors; integration run: 26 passed, four optional Node tests skipped because Node worker variables were not configured. Repository-wide format verification passes.

The GitHub project tracks the full plan in phase epics and implementation tickets. PR #57 contains the Phase 1 continuation; P1.1-P1.3 are In review, P1.4 accessibility review is In progress, and the phase epic remains In progress. See `docs/planning/README.md` for synchronization rules.

## Next implementation slice

Phase 1 continuation (2026-11-07): ADR-0002 defines version 1 as the first supported format; no pre-v1 migration input exists. The first version transition's preview/backups/idempotence are tracked in deferred ticket #60. The desktop edits project assets, compares disk content, reloads clean editors, and requires an explicit Use disk/Use draft conflict decision. Project and HTTP desktop smoke checks pass, including invalid-text recovery and second-writer read-only behavior. The five specification exit checks pass locally; see `docs/phase-1-exit-review.md`. The editor has access keys, a live status region, and programmatically checked UI Automation names and values. Manual Narrator, keyboard-only, high-contrast, and DPI review are tracked in In progress ticket #58. Branch/cache invalidation is tracked in #23, and execution from immutable snapshots in #59. PR #57 is open; P1.1-P1.3 are In review while Phase 1 remains In progress.

## Parallel review checkpoint (2026-11-07 20:37 IST)

Two agents reviewed code and acceptance evidence, with independent review of the fixes. The editor now preserves newer typing and recovery through save/cleanup awaits, reports immutable-ID errors, and requires another comparison when disk content changes before Use draft. Conflicting commands are blocked during operations and watcher refreshes are deferred.

Validation: one Release desktop build passed with zero warnings/errors; one --project-smoke run passed existing workflows and deterministic save/cleanup, noncanonical no-op, immutable-ID, and unseen-disk-revision regressions. Changed-file format verification passed. Local evidence and a synthetic editor screenshot are in ignored artifacts/phase1-review-smoke/run-20261107-203638. The screenshot is not manual accessibility evidence; PR screenshot attachment remains pending. Hosted checks for pre-fix commit 18dc2b6 passed; the pushed fix requires its own CI result. P1.3 returns to In review with PR #57; #58 and Phase 1 remain In progress. Manual accessibility is deferred at the user's request.

## Phase 2 Progress (2026-11-07 21:45 IST)

- **P2.1 (Issue #12, PR #61, commit `2559162`):** Request workbench, repeated query and header parameter preservation/ordering, multipart/urlencoded/file-backed streaming bodies, decompression, cookies, explicit TLS override, cross-origin redirect auth stripping, non-idempotent retry protection. Verified and merged into `main`.
- **P2.2 (Issue #13, PR #62, commit `62e6ea6`):** Variable resolution precedence, bounded nesting, circular reference detection, explicit non-shadowed `{{secret:...}}` namespace, Windows DPAPI `LocalSecretStore`, deterministic dynamic helpers, and `RedactionEngine`. Verified and merged into `main`.
- **P2.3 (Issue #14, PR #63, commit `cc50c7d`):** Authentication flows and lifecycle: Basic, Bearer, API key (header & query), OAuth 2.0 (client credentials, refresh token, PKCE authorization code with `OAuthPkceHelper`), Windows integrated authentication, Client certificates (mTLS via `X509CertificateLoader`), `RedactionEngine` credential stripping from previews and logs, and early rejection of unsupported authentication types (`UnsupportedAuthException`). 55 tests passed (0 failed, 4 skipped Node tests); Release desktop smoke passed; format verified. Merged into `main`.
- **P2.4 (Issue #54, PR #64, commit `ac6971f`):** Type-aware structured editors, viewers, and font preferences. Headless `StructuredTextEngine` provides format detection, beautification, and parse diagnostics for JSON, XML, YAML, GraphQL, and CSV without altering raw content or semantic values. Enforces large content threshold (5 MiB). AvalonEdit syntax highlighting wired to dynamic format detection and theme contrast. `LocalSettingsStore` persists editor font family and size with Segoe UI 12 pt (16 px) default. Workbench UI features font selectors, "Format body" command, structured vs. raw response view toggle, and diagnostic banners. 76 tests passed (0 failed, 4 skipped Node tests); Release desktop smoke passed; format verified. Merged into `main`.

## Phase 2 Complete (2026-11-07 22:00 IST)

Phase 2 (HTTP testing workbench) is complete. All 4 sub-issues (#12, #13, #14, #54) have been implemented, verified, and merged to `main`. All five Phase 2 specification exit criteria pass locally; see `docs/phase-2-exit-review.md`. Epic #11 closed and marked Done in GitHub Project 4. Advancing to Phase 3 (Collections and assertions).

## Phase 3 Complete (2026-11-07 22:40 IST)

Phase 3 (Collections, Assertions, and CLI) is complete. All 4 sub-issues (#16, #17, #18, #59) have been implemented, verified, and merged to `main`. All five Phase 3 specification exit criteria pass locally; see `docs/phase-3-exit-review.md`. Epic #15 closed and marked Done in GitHub Project 4. Ready to advance to Phase 4 (Datasets and clipboard).

- **P3.1 (Issue #16, PR #65, commit `efb21ec`):** Collection lifecycle, isolated iterations, and bounded flow control. Implemented `ExecutionModels.cs` and headless `CollectionRunner.cs` supporting explicit request ordering, collection setup/teardown with configurable policy (`Always`, `OnFailure`, `OnSuccess`), concurrency control, iteration isolation (deep-cloned variable scopes preventing cross-iteration mutable leakage), loop budgets (enforcing a maximum of 50 iterations per loop to prevent infinite loops), request selections, and graceful/forced cancellation. Integrated `run` command into `api-wrangler` CLI (`Program.cs`) and collection execution in `MainViewModel.cs`. 80 tests passed; Release desktop smoke passed; format verified. Merged into `main`.
- **P3.2 (Issue #17, PR #66, commit `ad1dbc6`):** Typed assertions, result aggregation, and evidence browser. Implemented `AssertionEngine.cs` with support for all typed assertions (`status`, `header`, `cookie`, `bodyContains`, `jsonPath`, `responseTime`, `responseSize`, `regex`) and operators (`equals`, `notEquals`, `contains`, `notContains`, `greaterThan`, `lessThan`, `exists`, `notExists`, `matches`). Evaluates assertions during collection and request execution. Distinguishes failed assertions (`ExecutionOutcome.Failed`) from transport/network errors (`ExecutionOutcome.Error`). Preserves teardown errors across aggregate calculations (`RunStatus.Error` / `RunStatus.Failed`). Implemented `BaselineEngine.cs` comparing status, headers, JSON and text with support for `IgnoredPaths`. Implemented reporters in `Reporters.cs` (`ConsoleReporter`, Section 14 `JsonReporter`, CI `JUnitReporter`, `MarkdownReporter`, `HtmlReporter`) and `EvidenceSanitizer` redacting sensitive headers and secret tokens. Wired `--reporters` and `--report-dir` into CLI `run` command and updated exit codes (exit code 1 for assertion failures, 3 for transport errors, 4 for cancellation, 0 for passed). 86 tests passed; Release desktop smoke passed; format verified. Merged into `main`.
- **P3.3 (Issue #18, PR #67, commit `8e733a1`):** Project CLI contract and CI examples. Extended `api-wrangler` CLI with `init` (canonical project scaffolding with `ProjectRepository.InitializeAsync`), `validate` (schema and reference integrity check), and enhanced `run` command with deterministic exit codes (0 for pass, 1 for failed assertions, 2 for invalid configuration/project, 3 for infrastructure/transport errors, 4 for cancellation, 5 for missing dependencies). Added host environment variable secret resolution (`API_WRANGLER_SECRET_<KEY>` or `<KEY>`) enabling noninteractive CI runs without interactive DPAPI keys. Added CI documentation and example workflows under `docs/ci/` (GitHub Actions, generic shell, Windows runner, Linux container runner) and `.github/workflows/regression-example.yml` with failure artifact retention (`if: always()`) and security best practices (no untrusted pull-request secret exposure, no `pull_request_target`). 90 tests passed; Release desktop smoke passed; format verified. Merged into `main`.
- **P3.4 (Issue #59):** Exact immutable snapshot execution. Connected Phase 1 immutable project snapshot store to desktop and CLI collection execution. Every run executes against an immutable content-addressed snapshot, preventing external file modifications during execution from altering results. Extended `ProjectSnapshots.CreateAsync` and `IProjectSession.SnapshotAsync` to support explicit in-memory dirty drafts (`EditorDraft`), writing draft contents into the isolated snapshot without overwriting saved project assets on disk. Recorded `SnapshotHash`, `GitCommit`, and `IsDirty` in run reports and results while keeping secrets omitted from metadata. 92 tests passed (0 failed, 4 skipped Node tests); Release solution build passed with 0 warnings/errors; desktop smoke passed; format verified. Ready for PR and merge into `main`.
