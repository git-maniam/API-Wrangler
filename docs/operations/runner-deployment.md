# Runner deployment status

No unattended runner or control plane is implemented. The current CLI is an interactive/CI-capable single-request foundation probe, not a hosted execution service.

Phase 10 must provide dedicated host credentials, scoped enrollment tokens, TLS, exact-commit isolated checkouts, snapshot admission, leases, heartbeats, cancellation, artifact limits, and an explicit indeterminate outcome after worker loss. Windows and Linux deployment instructions must be verified against actual packages before this document becomes a deployment runbook.

Never copy desktop Git credentials into a worker. Do not deploy the local fixture API as a public service.
