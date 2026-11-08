# Phase 6.1 Progress — Production Node Worker, Trust Policy, and Packages

**Date:** 2026-11-08 21:44 IST  
**Issue:** [Issue #27](https://github.com/git-maniam/API-Wrangler/issues/27)  
**Parent Epic:** [Issue #26](https://github.com/git-maniam/API-Wrangler/issues/26)  
**Branch:** `phase6-scripting-and-packages`  

---

## 1. Summary of Changes

1. **Repository Trust Policy & Storage (`ProjectTrustService`):**
   - Implemented host-local trust storage under local application data (`ApiWrangler/trust/trust.json`), never committing trust data into project repositories or Git assets.
   - Opening, cloning, fetching, and importing projects never execute scripts or install packages.
   - Computes deterministic SHA-256 script hash across all project scripts (`*.js`, `*.mjs`, `*.cjs`, `*.ts`, embedded `preRequestScript` / `testScript` in request/collection JSON) and package manifests (`package.json`, `package-lock.json`), strictly excluding `.git`, `node_modules`, `.local`, `runs`, and build outputs.
   - Detects script and manifest modifications made since trust was granted; requires re-authorization before running modified scripts.
   - Throws `UntrustedScriptExecutionException` with explicit disclosure that scripts run with full local user access and no security sandbox.

2. **Node Runtime Discovery & Diagnostics (`NodeRuntimeService`):**
   - Discovers Node and npm across environment variable `AWR_NODE`, local settings, system `PATH`, and standard platform locations (`Program Files/nodejs/node.exe`, `/usr/bin/node`, etc.).
   - Validates pinned LTS requirement (Node 20+ / 22+ / 26+).
   - Generates actionable diagnostics and supports missing dependency reporting (CLI Exit Code 5).

3. **Deterministic Package Management (`NpmPackageService`):**
   - Reads `package.json` and `package-lock.json` metadata (names, versions, dependencies, scripts) without running Node or executing scripts.
   - Enforces `npm ci --ignore-scripts` by default; lifecycle scripts (`preinstall`, `postinstall`, `prepare`) are never executed automatically.
   - Requires explicit project trust prior to package installation.
   - Verifies lockfile presence (`package-lock.json`), rejecting unpinned installations.
   - Separate, explicit lifecycle script execution (`RunScriptAsync`).
   - Relies on host-configured credentials (`~/.npmrc`), preventing private registry tokens from entering project assets.

4. **Production Node Worker & Framed Binary IPC (`workers/node/src/worker.ts` & `NodeScriptHost`):**
   - Binary framed IPC over stdin/stdout (4-byte big-endian length prefix + UTF-8 JSON payload).
   - Native `wrangler` API:
     - `wrangler.test(name, fn)`: executes test assertion blocks.
     - `wrangler.expect(actual)`: matchers (`toEqual`, `toBe`, `toBeTruthy`, `toBeFalsy`, `toBeNull`, `toBeDefined`, `toContain`, `toBeGreaterThan`, `toBeLessThan`, `toMatch`).
   - Context APIs:
     - `request`: url, method, headers, body, json(), text().
     - `response`: status, statusText, headers, body, json(), text(), responseTime, size.
     - Scopes: `variables`, `environment`, `collectionVariables`, `iterationData` (`get`, `set`, `has`, `unset`).
   - Bounded console logging (`console.log`, `info`, `warn`, `error`) capped at 64 KiB characters per run.
   - Hard termination of infinite loops (`while (true) {}`) and descendant processes via wall-clock timeout and process tree termination (`Process.Kill(entireProcessTree: true)`).

5. **Domain & Storage Integration:**
   - Added `PreRequestScript` and `TestScript` properties to `HttpRequestDefinition`.
   - Updated `RequestAssetSerializer` to serialize and deserialize script properties.

---

## 2. Verification

- **Automated Tests:** 213 passed, 0 failed, 0 skipped.
  - `ProjectTrustServiceTests`: 4 passed.
  - `NodeRuntimeAndPackageServiceTests`: 11 passed.
  - `NodeScriptHostTests`: 6 passed.
  - All existing Phase 0–5 suites passed (213 total).
- **Desktop Smoke:** `scripts/Test-Desktop.ps1` passed cleanly.
- **Formatter:** `dotnet format --verify-no-changes` passed with zero deviations.
- **TypeScript Worker:** `npm run build` and `npm run check` passed cleanly.
