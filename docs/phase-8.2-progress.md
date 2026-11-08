# Phase 8.2 Implementation Progress: Webhook Capture, Public Gateway, Forwarding, and Replay

**Date:** 2026-11-08  
**Issue:** [#35](https://github.com/users/git-maniam/projects/4) — `[P8.2] Webhook capture, public gateway, forwarding, and replay`  
**Parent Epic:** [#33](https://github.com/users/git-maniam/projects/4) — `[Phase 8] Mocks and webhook debugging`  
**Branch:** `phase8-webhook-debugging`

---

## 1. Summary of Work Delivered

Phase 8.2 delivers an enterprise-grade webhook capture listener, byte-preserving cryptographic signature verifier, secure destination forwarding/replay engine, and desktop UI debugger:

1. **Domain Models (`src/ApiWrangler.Domain/WebhookModels.cs`):**
   - `WebhookListenerDefinition`: Listener settings capturing `Id`, `Name`, `PathPrefix`, `SignatureType`, `SignatureHeader`, `SecretEnvironmentVariable`, response configurations, `AllowedForwardingDestinations`, `MaxBodySizeBytes`, and rate limits.
   - `WebhookSignatureType`: `None`, `HmacSha256Hex` (e.g. GitHub/Slack `sha256=...`), `HmacSha256Base64` (e.g. Stripe), `HmacSha1Hex`.
   - `WebhookCapturedEvent`: Immutable operational record preserving raw arrival timestamps, HTTP method, exact path/querystring, headers, byte-perfect `RawBody`, string body, signature validation result/diagnostic, and replay genealogy (`IsReplay`, `ParentEventId`, `EditNotes`).
   - `WebhookReplayRequest` and `WebhookReplayResult`: Replay definitions and execution audit records.

2. **Application Runtime Services (`src/ApiWrangler.Application/WebhookService.cs`):**
   - **`WebhookService`:** Lightweight in-process ASP.NET Core listener capturing incoming webhooks.
   - **Byte-Perfect Signature Verification:** `VerifySignature` computes cryptographic HMACs directly against the raw unparsed payload bytes using constant-time comparisons (`CryptographicOperations.FixedTimeEquals`). Never parses or reserializes payloads before verifying signatures.
   - **Operational Safeguards:** Enforces `MaxBodySizeBytes` (returning 413 Payload Too Large) and strictly validates destination policy (`AllowedForwardingDestinations`) before forwarding to prevent SSRF or unrestricted open proxy abuse.
   - **Exact-Byte & Edited Replay:** Replays exact raw bytes preserving original header structures, or dispatches edited payloads logged with distinct audit records and child lineage.

3. **Desktop Workbench Integration (`src/ApiWrangler.Desktop`):**
   - Upgraded `MockServerViewModel.cs` with reactive Webhook service controls (`ToggleWebhookServiceCommand`, `RefreshWebhookEventsCommand`, `WebhookStatus`, `WebhookListeningUrl`, `WebhookCapturedCountText`, `RawWebhookEvents`).
   - Updated `MainWindow.xaml` adding a "Webhook Debugger" tab in the "Mocks & Webhooks" panel to inspect captured payloads, signature status, and replay history.

4. **Integration Test Suite (`tests/ApiWrangler.IntegrationTests/WebhookServiceTests.cs`):**
   - `WebhookService_VerifySignature_HmacSha256Hex_MatchesExactRawBytes`: Verifies HMAC-SHA256 signature matching against raw bytes, and verifies that whitespace tampering fails verification.
   - `WebhookService_Capture_RecordsRawBytesAndHeaders`: Tests HTTP webhook ingestion, response generation, and header/payload capture.
   - `WebhookService_Replay_BytePreservingAndDestinationPolicy`: Verifies byte-preserving replay to allowed internal destinations, verified audit event logging, and blocking of disallowed external destinations according to destination policy.

---

## 2. Verification Summary

- **Automated Tests:** 249 passed across the full test suite in Release configuration (`0` failed, `0` skipped).
- **Code Formatting:** `dotnet format --verify-no-changes` exited with code `0`.
- **Desktop UI Smoke Verification:** `scripts/Test-Desktop.ps1` passed with `{"editorRoundTrip":true,"httpRoundTrip":true,"cancellation":true,"dispatcherTicksDuringRequest":9}`.
