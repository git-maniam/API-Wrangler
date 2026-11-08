# Phase 10.3 Implementation Progress: Scheduler Ownership, Time Policy, and Notifications

**Date:** 2026-11-09  
**Issue:** [#42](https://github.com/users/git-maniam/projects/4) — `[P10.3] Scheduler ownership, time policy, and notifications`  
**Parent Epic:** [#39](https://github.com/users/git-maniam/projects/4) — `[Phase 10] Unattended execution and monitoring`  
**Branch:** `phase10-scheduler-notifications`

---

## 1. Summary of Work Delivered

Phase 10.3 implements scheduled execution ownership, IANA time zone support, missed run backlog suppression, overlap prevention, and an authenticated notification engine with cooldowns and recovery alerts:

1. **Domain Models (`src/ApiWrangler.Domain/SchedulerModels.cs`):**
   - `ScheduleType`: `Interval`, `Cron`.
   - `ScheduleOverlapPolicy`: `Skip`, `Queue`, `TerminateExisting`.
   - `ScheduleMissedRunPolicy`: `SkipMissedIntervals`, `RunOnceImmediately`.
   - `NotificationSeverity`: `Info`, `Warning`, `Failure`, `Recovery`.
   - `NotificationRoute`: Webhook URL, secret environment variable name (`SecretEnvVarName`), failure/recovery delivery flags, and cooldown durations.
   - `ScheduleDefinition`: Captures schedule ID, collection, environment, interval, IANA time zone (`IanaTimeZoneId`), designated deployment ownership (`DesignatedDeploymentId`), overlap/missed-run policies, pinned approved commit SHA (`ApprovedCommitSha`), required capabilities, and notification routes.
   - `ScheduleExecutionRecord`: Tracks scheduled vs actual execution times, target time zone ID, and dispatch status (`Dispatched`, `SkippedOwnership`, `SkippedOverlap`).
   - `NotificationEvent` & `NotificationDeliveryDiagnostic`: Structured delivery telemetry including status code, suppression status, and error details.

2. **Application Runtime Services (`src/ApiWrangler.Application/SchedulerService.cs`):**
   - **`SchedulerEngine`:**
     - **Designated Deployment Ownership:** Ensures that opening the repository on multiple clients or laptops never duplicates monitoring. Only the designated deployment node (`CurrentDeploymentId == DesignatedDeploymentId`) executes active schedules.
     - **Time & DST Semantics:** Accurately converts tick timestamps to target IANA time zones (`TimeZoneInfo.ConvertTime`).
     - **Missed Run Policy:** Default to skipping missed intervals after downtime (`SkipMissedIntervals`) rather than creating an uncontrolled backlog.
     - **Overlap Prevention:** Detects in-flight executions for the schedule and applies policy (`Skip` skips tick without re-dispatching).
     - **Pinned Commit Approval:** Dispatches jobs targeting approved commit SHAs rather than unverified branch tips.
   - **`NotificationDispatcher`:**
     - **Host-Local Secret Resolution:** Resolves webhook authentication tokens directly from host environment variables (`route.SecretEnvVarName`), ensuring zero secret exposure in Git.
     - **Failure Cooldown & Deduplication:** Tracks previous notifications and suppresses repeated alert floods within `CooldownPeriod`.
     - **Recovery Alerts:** Automatically emits a recovery notification when a failing schedule transitions back to a successful state.
     - Full delivery diagnostics recording HTTP status codes and error messages.

3. **Integration Test Suite (`tests/ApiWrangler.IntegrationTests/SchedulerServiceTests.cs`):**
   - `Scheduler_DesignatedDeploymentOwnership_PreventsDuplicateRunsAcrossMultipleClients`: Validates that client laptops ignore schedules designated for production runner nodes.
   - `Scheduler_TimeZonesAndDowntime_SkipsMissedIntervalsWithoutBacklog`: Validates that 2 hours of downtime does not produce a 24-run backlog.
   - `Scheduler_OverlapPrevention_SkipsExecutionWhenPreviousRunIsActive`: Validates that overlapping runs are skipped when a previous run is still in-flight.
   - `NotificationDispatcher_EnforcesCooldown_DeduplicatesAlerts`: Validates that consecutive failures within cooldown periods are cleanly suppressed.
   - `NotificationDispatcher_SendsRecoveryAfterFailure_AndUsesHostSecrets`: Validates Bearer token authorization headers from host secrets and recovery notification dispatch following failure.

---

## 2. Verification Summary

- **Automated Tests:** 273 passed across the full test suite in Release configuration (`0` failed, `0` skipped).
- **Code Formatting:** `dotnet format --verify-no-changes` passed with zero deviations.
- **Desktop UI Smoke Verification:** `scripts/Test-Desktop.ps1` passed with `{"editorRoundTrip":true,"httpRoundTrip":true,"cancellation":true,"dispatcherTicksDuringRequest":9}`.

