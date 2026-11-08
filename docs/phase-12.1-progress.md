# Phase 12.1 Progress Report — Playwright Package, Traffic Matching, and Combined Results

## Objective
Implement Playwright application testing integration, enabling network traffic capture from browser test suites, matching against API Wrangler collection definitions, evaluating API assertions on live browser traffic, redacting sensitive credentials/cookies, highlighting explicit limitations (caching, service worker, streaming, WebSocket), building dependency maps, bootstrapping draft requests from unmatched traffic, and emitting structured CI reports.

## Deliverables

1. **Domain Models (`PlaywrightModels.cs`)**:
   - `CapturedTrafficRecord`: Stores captured network interactions including method, URL, headers, bodies, duration, status, and browser flags (`IsCached`, `IsServiceWorker`, `IsWebSocket`, `IsTruncated`).
   - `TrafficMatchResult`: Represents scoring and ambiguity detection when comparing traffic against collection requests.
   - `TrafficAssertionRule` & `CapturedTrafficValidationResult`: Evaluates status codes, headers, and body JSON properties against expected criteria.
   - `PlaywrightCombinedScenarioResult`: Unifies UI scenario status and API assertion status, naming failing scenarios and primary root causes.
   - `TrafficDependencyMap`: Models sequential request chains and interaction counts.
   - `PlaywrightSummaryReport`: Aggregates pass/fail counts, unmatched and ambiguous request metrics.

2. **TypeScript Interceptor Package (`workers/node/src/playwright.ts`)**:
   - `PlaywrightTrafficCollector`: Listens to `page.on('request')` and `page.on('response')`, calculates duration, redacts sensitive headers and JSON values, bounds body payloads to 256 KiB, identifies service worker interceptions, cache hits, and WebSocket upgrades, and writes `.api-wrangler/captures/playwright-traffic.json`.
   - `attachApiWranglerCollector`: Helper function for seamless integration into existing Playwright tests.

3. **Application Validation Service (`PlaywrightTrafficService.cs`)**:
   - `RedactTraffic`: Redacts sensitive headers (`Authorization`, `Cookie`, `Set-Cookie`, `X-Api-Key`, etc.) and sensitive JSON keys (`password`, `secret`, `token`, `apiKey`, etc.), and truncates large payloads.
   - `MatchTraffic`: Specificity matching on origin, method, exact/parameterized/wildcard path templates, and headers, with deterministic precedence and ambiguity flags.
   - `ValidateTrafficRecord`: Evaluates status codes, response headers, and response body JSON values using typed comparisons (`Equals`, `LessThan`, `GreaterThan`, `Contains`, `Regex`).
   - `EvaluateScenario`: Evaluates browser scenario traffic. If UI assertions pass but an API assertion fails, marks combined outcome as `Failed`, assigns failure source as `API`, and names the responsible scenario. Discloses encountered browser limitations (`BrowserHttpCache`, `ServiceWorkerInterception`, `WebSocketTraffic`, `BoundedPayloadLimit`).
   - `BuildDependencyMap`: Generates graph of endpoints and call frequencies observed during scenario execution.
   - `GenerateDraftRequests`: Bootstraps draft `HttpRequestDefinition` assets from unmatched traffic.
   - `GenerateCiArtifacts`: Emits machine-readable `playwright-api-report.json` and human-readable `playwright-api-summary.md`.

4. **CLI Integration (`ApiWrangler.Cli/Program.cs`)**:
   - Added `api-wrangler playwright evaluate --capture <path> --requests <path> --scenario <name> --ui-passed <true/false> --output-dir <path>`.

5. **Automated Verification (`PlaywrightTrafficTests.cs`)**:
   - Verified that a passing UI scenario with a failing API assertion fails the combined result and explicitly names the scenario (`"Browser Checkout Flow"`).
   - Verified that ambiguous and unmatched traffic is surfaced, and credentials/cookies are redacted.
   - Verified that caching, service worker, and WebSocket limitations are disclosed.
   - Verified CI artifact generation (`playwright-api-report.json`, `playwright-api-summary.md`).
   - Verified dependency graph mapping and draft request generation.

