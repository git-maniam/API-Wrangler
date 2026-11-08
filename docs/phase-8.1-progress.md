# Phase 8.1 Implementation Progress: Local Mock Engine, Deterministic Matching, and Fault Scenarios

**Date:** 2026-11-08  
**Issue:** [#34](https://github.com/users/git-maniam/projects/4) — `[P8.1] Local mock engine, deterministic matching, and fault scenarios`  
**Parent Epic:** [#33](https://github.com/users/git-maniam/projects/4) — `[Phase 8] Mocks and webhook debugging`  
**Branch:** `phase8-local-mock-engine`

---

## 1. Summary of Work Delivered

Phase 8.1 implements a robust, lightweight, in-process ASP.NET Core local mock server engine with deterministic matching, stateful scenario transitions, fault simulations, and desktop UI management:

1. **Domain Models (`src/ApiWrangler.Domain/MockModels.cs`):**
   - `MockRule`: Definable rule capturing Method, Path pattern (exact, wildcard, parameterized `{id}`), Priority, Query parameters, Headers, Body regex/substring pattern, status code, response headers, response body, delay, fault simulation (`MockFaultType`), intermittent failure rate with recorded seeds, and scenario state prerequisites/transitions.
   - `MockFaultType`: `None`, `HttpError`, `DelayedResponse`, `ConnectionTermination`, `MalformedPayload`, `RateLimit`.
   - `MockServerLogEntry`: Complete audit log entry capturing method, path, query, headers, body, matched rule count/id, response status, fault applied, and execution duration.
   - `MockServerConfiguration`: Local-first configuration enforcing loopback binding (`127.0.0.1`) by default.
   - `MockMatchResult`: Deterministic evaluation with specificity scoring, explanations, and ambiguity detection.

2. **Application Runtime Services (`src/ApiWrangler.Application/MockEngine.cs`, `MockExampleGenerator.cs`):**
   - **`MockEngine`:** In-process ASP.NET Core server dynamically hosting configured mock endpoints on loopback ports.
   - **Deterministic Matching Precedence:** Priority weighting (`Priority * 1000`) combined with path specificity (exact > parameterized > wildcard > catch-all), query param counts, header matches, and body regex matches. Detects and reports ambiguous rules.
   - **Stateful Scenarios:** Transitions scenario state machines across sequential calls (e.g., checkout/order flows) and supports state resets.
   - **Fault Simulation Engine:** Injects delays, simulated HTTP errors (5xx/4xx), rate limits (429 with `Retry-After`), connection abortions (`context.Abort()`), malformed payloads, and seeded intermittent failures.
   - **`MockExampleGenerator`:** Generates mock rules from OpenAPI schemas and saved responses.

3. **Desktop Workbench Integration (`src/ApiWrangler.Desktop`):**
   - Added `MockServerViewModel.cs` with reactive rule creation, start/stop server controls, external binding toggle (explicit opt-in, never automatic), live request log inspector, and state reset.
   - Updated `MainWindow.xaml` adding a "Mocks & Webhooks" tab with a split view for rule configuration and live request audit logs.

4. **Integration Test Suite (`tests/ApiWrangler.IntegrationTests/MockEngineTests.cs`):**
   - `MockEngine_Matching_SpecificityAndPrecedence_WorksDeterministically`: Verifies exact path precedence over parameterized (`{id}`) and wildcard paths.
   - `MockEngine_StatefulScenarios_TransitionsStateCorrectly`: Validates state machine progression.
   - `MockEngine_HttpHosting_ServesRequestsAndLogs`: Validates end-to-end HTTP request processing, response headers, body, and audit logging.
   - `MockEngine_FaultSimulation_HttpErrorAndDelay`: Verifies fault simulation dispatching 502 Bad Gateway and audit recording.
   - `MockEngine_ExternalBinding_DefaultsToLoopback`: Verifies that server strictly binds to loopback (`127.0.0.1`) without explicit opt-in.

---

## 2. Verification Summary

- **Automated Tests:** 246 passed across the full test suite in Release configuration (`0` failed, `0` skipped).
- **Code Formatting:** `dotnet format --verify-no-changes` exited with code `0`.
- **Desktop UI Smoke Verification:** `scripts/Test-Desktop.ps1` passed with `{"editorRoundTrip":true,"httpRoundTrip":true,"cancellation":true,"dispatcherTicksDuringRequest":9}`.
