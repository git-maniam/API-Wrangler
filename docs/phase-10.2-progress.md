# Phase 10.2 Implementation Progress: Control Plane, Fenced Leases, Artifacts, and Crash Recovery

**Date:** 2026-11-09  
**Issue:** [#41](https://github.com/users/git-maniam/projects/4) — `[P10.2] Control plane, fenced leases, artifacts, and crash recovery`  
**Parent Epic:** [#39](https://github.com/users/git-maniam/projects/4) — `[Phase 10] Unattended execution and monitoring`  
**Branch:** `phase10-control-plane-leases`

---

## 1. Summary of Work Delivered

Phase 10.2 implements the core control plane responsibilities: an outbound polling queue with strictly monotonic fenced ownership, crash and lease expiration reconciliation that marks lost mutating runs as indeterminate, cancellation propagation, and a secure artifact storage service supporting size caps, SHA-256 verification, and retention sweeps:

1. **Domain Models (`src/ApiWrangler.Domain/ControlPlaneModels.cs`):**
   - `ControlPlaneJobQueueItem`: Tracks job ID, definition, execution status, assigned runner, lease ID, monotonic fencing token, enqueued/lease expiration/heartbeat timestamps, attempt count, mutating vs non-mutating classification, and cancellation requested state.
   - `ArtifactBackendType`: `LocalFilesystem`, `SharedStorage`, `GitHubActions`, `S3Compatible`.
   - `ArtifactMetadata`: Captures artifact ID, job ID, name, backend type, size in bytes, MIME content type, SHA-256 checksum, expiration timestamp, and retention flag.
   - `ArtifactPolicyConfig`: Enforces maximum file size limit (default 50 MiB), retention period, and failure-only retention option.
   - `ControlPlaneHealthReport`: Health status, active runners, queued jobs, active leases, storage usage, and diagnostic logs.

2. **Application Runtime Services (`src/ApiWrangler.Application/ControlPlaneService.cs`):**
   - **`ControlPlaneQueue`:**
     - Outbound polling: `AcquireJobLease` matches runner capabilities against required job labels and issues a lease with a strictly increasing monotonic `FencingToken`.
     - Fenced ownership: `HeartbeatLease` and `CompleteJob` strictly verify that the caller holds the current fencing token; stale or superseded runners cannot renew or complete jobs, eliminating split-brain races.
     - Cancellation signaling: `RequestCancellation` sets the cancellation flag, which is immediately returned to the runner on its next heartbeat.
     - Crash & Expiration Recovery (`ReconcileExpiredLeases`):
       - **Mutating jobs:** When a runner holding a mutating job crashes or its lease expires, the job outcome is explicitly marked **`Indeterminate`** with full diagnostics, rather than silently retried.
       - **Non-mutating jobs:** Can be safely re-enqueued for another attempt within its configured `MaxAttempts` budget.
   - **`ArtifactStorageService`:**
     - Enforces streaming size policy (`MaxSizeBytes`), deleting partial writes and throwing `InvalidOperationException` if limits are exceeded.
     - On-the-fly SHA-256 calculation (`IncrementalHash`) during streaming.
     - Retention sweep (`ApplyRetentionSweep`): deletes expired operational artifacts from disk while preserving marked reports and retained files.
     - Health checks (`GetHealthReport`): checks filesystem accessibility, total storage bytes, and active queue telemetry.

3. **Integration Test Suite (`tests/ApiWrangler.IntegrationTests/ControlPlaneTests.cs`):**
   - `ControlPlane_AtLeastOnceDispatch_AndFencedOwnership`: Validates capability-based lease acquisition, monotonic fencing tokens, heartbeat extensions, and rejection of completions with stale fencing tokens.
   - `ControlPlane_LostMutatingJob_BecomesIndeterminate_NotSilentlyRetried`: Validates that lost mutating jobs become `Indeterminate` upon lease expiration and are not silently retried.
   - `ControlPlane_LostNonMutatingJob_RetriesWithinBudget`: Validates safe re-enqueueing of lost read-only jobs within attempt limits.
   - `ControlPlane_Cancellation_PropagatesToRunnerDuringHeartbeat`: Validates cancellation propagation during heartbeat calls.
   - `ArtifactStorage_EnforcesMaxSizeBytes_AndCalculatesChecksum`: Validates streaming SHA-256 checksums and size limit enforcement.
   - `ArtifactStorage_RetentionSweep_CleansExpiredOperationalArtifacts`: Validates retention policy sweeps and storage health reporting.

---

## 2. Verification Summary

- **Automated Tests:** 268 passed across the full test suite in Release configuration (`0` failed, `0` skipped).
- **Code Formatting:** `dotnet format --verify-no-changes` passed with zero deviations.
- **Desktop UI Smoke Verification:** `scripts/Test-Desktop.ps1` passed with `{"editorRoundTrip":true,"httpRoundTrip":true,"cancellation":true,"dispatcherTicksDuringRequest":8}`.

