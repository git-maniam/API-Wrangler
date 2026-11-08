# Phase 9.2 Implementation Progress: Latency Histograms, Thresholds, and Performance Reports

**Date:** 2026-11-08  
**Issue:** [#38](https://github.com/users/git-maniam/projects/4) — `[P9.2] Latency histograms, thresholds, and performance reports`  
**Parent Epic:** [#36](https://github.com/users/git-maniam/projects/4) — `[Phase 9] Local performance testing`  
**Branch:** `phase9-histograms-reports`

---

## 1. Summary of Work Delivered

Phase 9.2 implements true histogram percentile aggregation, threshold evaluation policies, distinct failure categorization, and updated reporting across domain, engine, and UI:

1. **Domain Models (`src/ApiWrangler.Domain/LoadTestModels.cs`):**
   - `LoadThresholdPolicy`: Configurable constraints for `MaxP95LatencyMs`, `MaxP99LatencyMs`, `MaxErrorRatePercent`, and `MinThroughputRps`.
   - `LoadThresholdEvaluation`: Captures binary `Passed` status alongside explicit, descriptive `Violations` collection.
   - Updated `LoadTestReport` to include evaluated `ThresholdEvaluation`.

2. **Application Runtime Services (`src/ApiWrangler.Application/LoadExecutionEngine.cs`):**
   - **`CalculateHistogram`:** Direct calculation of exact percentiles (min, mean, max, p50, p90, p95, p99) derived from sorted latency measurements rather than invalidly averaging percentiles.
   - **`MergeHistograms`:** Multi-worker and multi-run aggregation method that combines raw latency pools and recomputes true percentiles across the whole distribution.
   - **`EvaluateThresholds`:** Evaluates latency thresholds (p95, p99), error rate thresholds, and throughput limits against report data, generating specific violation diagnostic messages.
   - **Monotonic Duration Measurement:** Uses `Stopwatch.StartNew()` and `Stopwatch.GetTimestamp()` with high-resolution performance counters to ensure duration calculations are monotonic and immune to system clock shifts.
   - **Distinct Failure Categorization:** Categorizes functional HTTP failures (e.g. `HTTP 500`), transport/network exceptions (e.g. `HttpRequestException`, connection termination), and threshold policy failures distinctly in reports.

3. **Desktop Workbench Integration (`src/ApiWrangler.Desktop`):**
   - Added threshold properties `MaxP95LatencyMs` and `MaxErrorRatePercent` to `LoadTestingViewModel.cs`.
   - Added user controls in `MainWindow.xaml` for configuring latency and error rate thresholds.
   - Formatted report summaries to display threshold evaluations (`PASSED` or detailed violation lists) and categorized error breakdowns.

4. **Integration Test Suite (`tests/ApiWrangler.IntegrationTests/LoadExecutionEngineTests.cs`):**
   - `LoadEngine_MergeHistograms_MergesAccuratePercentilesFromCombinedPool`: Validates that merging histograms combines samples into a unified distribution and computes true percentiles without averaging errors.
   - `LoadEngine_EvaluateThresholds_EvaluatesPassAndDistinctViolations`: Validates passing threshold evaluations and tests that violations for p95, p99, error rate, and minimum throughput are reported with clarity.
   - `LoadEngine_FailureCategorization_DistinguishesFunctionalVsTransportVsThresholdFailures`: Validates that functional HTTP 500 status codes, network/socket connection terminations, and threshold policy violations are captured distinctly.

---

## 2. Verification Summary

- **Automated Tests:** 257 passed across the full test suite in Release configuration (`0` failed, `0` skipped).
- **Code Formatting:** `dotnet format --verify-no-changes` passed with zero deviations.
- **Desktop UI Smoke Verification:** `scripts/Test-Desktop.ps1` passed with `{"editorRoundTrip":true,"httpRoundTrip":true,"cancellation":true,"dispatcherTicksDuringRequest":10}`.
