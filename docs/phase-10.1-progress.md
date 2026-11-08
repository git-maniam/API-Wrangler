# Phase 10.1 Implementation Progress: Runner Service, Enrollment, and Exact-Snapshot Execution

**Date:** 2026-11-09  
**Issue:** [#40](https://github.com/users/git-maniam/projects/4) — `[P10.1] Runner service, enrollment, and exact-snapshot execution`  
**Parent Epic:** [#39](https://github.com/users/git-maniam/projects/4) — `[Phase 10] Unattended execution and monitoring`  
**Branch:** `phase10-runner-enrollment`

---

## 1. Summary of Work Delivered

Phase 10.1 implements unattended runner architecture, scoped cryptographic token enrollment, rotation, revocation, exact-commit execution validation, host secret isolation, capability label matching, and CLI commands:

1. **Domain Models (`src/ApiWrangler.Domain/RunnerModels.cs`):**
   - `RunnerPlatform`: `Windows`, `Linux`, `Container`.
   - `RunnerStatus`: `Offline`, `Idle`, `Busy`, `Draining`.
   - `RunnerEnrollmentScope`: `RunnerRegistration`, `JobSubmission`, `Admin`.
   - `RunnerJobOutcome`: `Queued`, `Assigned`, `Running`, `Passed`, `Failed`, `Indeterminate`, `Cancelled`.
   - `RunnerDescriptor`: Captures runner ID, platform, capability labels, and registration timestamps.
   - `RunnerEnrollmentToken`: Stores token ID, hashed secret, scope, expiration, revocation status, and allowed capabilities.
   - `RunnerJobDefinition`: Requires exact commit SHA, repository URL, project subdirectory, execution type, timeouts, and artifact policies. Crucially, no plaintext long-lived credentials are included in the job document.
   - `RunnerJobResult`: Captures outcome, exit code, summary, executed commit SHA, and explicit `IndeterminateReason` for unexpected terminations.

2. **Application Runtime Services (`src/ApiWrangler.Application/RunnerService.cs`):**
   - **`RunnerEnrollmentService`:**
     - Generates 256-bit cryptographically secure enrollment tokens (`awr_tok_...`).
     - Stores only SHA-256 hashes of secrets.
     - Constant-time verification (`CryptographicOperations.FixedTimeEquals`) to prevent timing side channels.
     - Role-based scope enforcement (Admin scope inherits all permissions).
     - Expiration checks and explicit revocation support.
     - Atomic token rotation revoking the predecessor and issuing a new token with inherited metadata.
   - **`ExactCommitJobExecutor`:**
     - Enforces exact commit execution (rejects missing or invalid commit SHAs).
     - Isolates job executions into dedicated working directories.
     - Generates execution manifest recording exact commit, engine version, host machine, and dedicated account.
     - Dedicated host secrets isolation: resolves `API_WRANGLER_SECRET_*` variables locally without uploading or extracting desktop credentials.
     - Handles crashes during mutating jobs by marking the outcome as `Indeterminate` rather than silently retrying.
     - Capability label matching logic (`MatchCapabilities`).

3. **CLI Operations (`src/ApiWrangler.Cli/Program.cs`):**
   - `runner enroll`: Scoped token generation with `--name`, `--scope`, and `--validity-days`.
   - `runner exec`: Headless unattended execution of exact-commit jobs in isolated directories.

4. **Integration Test Suite (`tests/ApiWrangler.IntegrationTests/RunnerServiceTests.cs`):**
   - `RunnerEnrollment_GeneratesValidToken_AndEnforcesScopes`: Validates token format, scope permissions, and Admin inheritance.
   - `RunnerEnrollment_EnforcesExpiration_AndRevocation`: Validates expiration rejection, manual revocation, and token rotation.
   - `ExactCommitJobExecutor_EnforcesExactCommitSha_AndWritesManifest`: Validates exact commit SHA enforcement and execution manifest generation.
   - `ExactCommitJobExecutor_CrashesYieldIndeterminateOutcome_PreventingSilentRetries`: Validates that process terminations during potentially mutating runs produce `Indeterminate` outcome with diagnostics.
   - `ExactCommitJobExecutor_MatchesCapabilityLabels_AndResolvesHostSecrets`: Validates capability set filtering and host environment secret isolation.

---

## 2. Verification Summary

- **Automated Tests:** 262 passed across the full test suite in Release configuration (`0` failed, `0` skipped).
- **Code Formatting:** `dotnet format --verify-no-changes` passed with zero deviations.
- **Desktop UI Smoke Verification:** `scripts/Test-Desktop.ps1` passed with `{"editorRoundTrip":true,"httpRoundTrip":true,"cancellation":true,"dispatcherTicksDuringRequest":8}`.

