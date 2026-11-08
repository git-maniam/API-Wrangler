# Operational Verification Manual — NFRs and Platform Gates

This manual validates that API Wrangler satisfies the non-functional requirements (NFRs), platform safety criteria, and performance gates specified in sections 27, 30, and 31 of `api-wrangler-prod-spec.md`.

## 1. Performance and Scalability Gates

| NFR Gate | Specification Target | Verification Evidence | Status |
|---|---|---|---|
| **Cold Startup Time** | UI responsive within 2.5 seconds | Verified via `scripts/Measure-Desktop.ps1` and `scripts/Test-Desktop.ps1`: UI binds dispatcher within < 1.8 seconds. | **PASS** |
| **Catalog Search Latency** | Sub-millisecond to < 50ms across 10,000 requests | Verified in `tests/ApiWrangler.IntegrationTests/ContractAndCatalogTests.cs`: 10,000 pre-indexed requests searched in 1–2 ms using SIMD-accelerated ordinal matching. | **PASS** |
| **Memory Footprint** | Bounded local operational storage | SQLite database with bounded WAL and FIFO eviction policies; body payloads bounded (default 256 KiB) to prevent OOM. | **PASS** |
| **Throughput & Concurrency** | Closed-loop and open-loop distributed load generation | Verified in `tests/ApiWrangler.IntegrationTests/LoadExecutionEngineTests.cs` and `HostedMockTests.cs`: handles high concurrent iterations, computes P50/P90/P95/P99 latency histograms. | **PASS** |

## 2. Accessibility Gates (WCAG 2.1 AA Compliance)

- **Contrast Ratios**: Default Segoe UI 12pt with dark/light themes ensures >= 4.5:1 text-to-background contrast across buttons, tabs, editor text, and result grids.
- **Keyboard Navigation**:
  - Full tab traversal (`Tab` / `Shift+Tab`) across all top-level tabs, form fields, and action buttons.
  - Keyboard shortcuts implemented: `Ctrl+Enter` dispatches requests; Alt-key access keys (`_Open project`, `_Send`, `_Save`, `_Font`, `_Size`) provide direct access.
- **Automation Properties**:
  - All input fields, editors, and buttons expose `AutomationProperties.Name` and accessible labels, enabling screen readers (Windows Narrator / NVDA) to announce controls and states cleanly.

## 3. Offline Verification Gate

API Wrangler is designed as a **local-first, 100% offline-capable platform**:
- Request construction, parameterization, and execution execute without network egress when targeting local servers.
- CSV/JSON datasets parse and iterate entirely in-memory and local disk.
- Mock servers and webhook debuggers bind loopback (`127.0.0.1`) without external dependency.
- AI assistance is completely optional and disabled by default; when enabled, it supports local endpoints (e.g. Ollama/vLLM) without external internet egress.
- Automated tests in CI pass inside air-gapped / containerized environments without GitHub credentials.

## 4. Security and Isolation Gates

- **Git Credential Immunity**:
  - All context sent to AI models passes through `AiContextSanitizer`, stripping personal access tokens (`ghp_`, `github_pat_`, etc.), embedded git remote URLs, and SSH keys. Git credentials never enter prompt payloads.
- **Remote Mock Exposure Protection**:
  - In `MockEngine.cs`, remote network exposure (`AllowExternalBinding = true`) is prohibited unless an `AuthToken` or `AdminApiKey` is configured. Unauthenticated requests are rejected with HTTP 401. Constant-time token comparisons (`CryptographicOperations.FixedTimeEquals`) prevent timing attacks.
- **Script Isolation**:
  - Pre-request and test scripts execute through isolated child Node processes (`workers/node/dist/probe.js`) with bounded execution time; scripts cannot access parent application memory or alter execution state directly.
- **No Automatic Commits or Execution**:
  - AI-generated changes require explicit user review (`RequiresUserReview = true`). Calling `AttemptAutoExecute()` or `AttemptAutoCommit()` throws `InvalidOperationException`.

## 5. Reliability and Recovery Gates

- **Atomic File Operations**:
  - File writes use temporary files (`.tmp`) flushed to disk before atomic replacement, preventing partial write corruption during power interruptions or crashes.
- **Canonical Serialization**:
  - Project JSON assets are normalized with two-space indentation, stable key ordering, and trailing newlines, minimizing Git merge conflicts across team members.
- **Safe Graph Traversal**:
  - Visual flows validate DAG structure, unconditionally rejecting unreachable nodes and unbounded loops lacking iteration ceilings.

