# Hosted Mock Server Deployment and Operations

## 1. Overview

API Wrangler mock servers provide high-fidelity HTTP simulation using identical rule definitions whether executed locally on loopback inside the desktop UI or headlessly as a shared team service. No proprietary cloud service, SaaS subscription, or remote vendor accounts are required.

## 2. Security and Network Binding Architecture

### Loopback Enforcement
By default, the mock engine binds strictly to `127.0.0.1` (loopback). Attempting to configure an external host such as `0.0.0.0` without enabling `AllowExternalBinding` is ignored, falling back safely to loopback to prevent accidental intranet or internet exposure.

### Mandatory Authentication on Remote Exposure
In accordance with specification Section 17.4, remote exposure (`AllowExternalBinding: true`) strictly requires configuring an authentication token (`AuthToken`). Starting an externally-bound mock server without an authentication token throws an `InvalidOperationException` and aborts startup immediately:
```text
Remote exposure requires explicit authentication configuration (AuthToken). Starting a mock server with external binding without authentication is not permitted.
```

### Constant-Time Token Verification
All authentication verification compares tokens using constant-time cryptographic equality (`CryptographicOperations.FixedTimeEquals`), mitigating timing side-channel attacks.

Incoming requests to authenticated mock servers must supply either:
- `Authorization: Bearer <token>`
- `X-Api-Key: <token>`

Unauthenticated requests are rejected immediately with HTTP 401 Unauthorized.

## 3. Administrative Control Plane

Shared mock instances expose administrative endpoints prefixed with `/__admin/`:

| Endpoint | Method | Authentication | Description |
|---|---|---|---|
| `/__admin/health` | GET | Public | Health, listening URL, active rule counts, and configuration flags. |
| `/__admin/rules` | GET | Admin / Auth Token | Returns active mock rule definitions. |
| `/__admin/logs` | GET | Admin / Auth Token | Returns recorded request logs up to the configured retention cap. |
| `/__admin/state/reset` | POST | Admin / Auth Token | Atomically clears scenario states, clears logs, and resets random seeds. |

## 4. Bounded Retention & Memory Protection

Long-running headless services protect memory by capping stored log entries at `MaxLogRetention` (default: 1,000 entries). When the cap is reached, the oldest log entries are trimmed FIFO without unbounded memory consumption or leaks.

## 5. Deployment Options

### Standalone CLI Execution
```bash
api-wrangler mock serve \
  --rules ./project/mocks/rules.json \
  --host 0.0.0.0 \
  --port 8080 \
  --allow-external \
  --auth-token "${MOCK_AUTH_TOKEN}" \
  --admin-key "${MOCK_ADMIN_KEY}" \
  --max-logs 2000
```

### Containerized Deployment (Docker / Podman)
```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:10.0-preview
WORKDIR /app
COPY ./publish .
COPY ./rules.json /etc/api-wrangler/rules.json
EXPOSE 8080
ENV MOCK_AUTH_TOKEN=""
ENV MOCK_ADMIN_KEY=""
ENTRYPOINT ["dotnet", "ApiWrangler.Cli.dll", "mock", "serve", \
            "--rules", "/etc/api-wrangler/rules.json", \
            "--host", "0.0.0.0", "--port", "8080", \
            "--allow-external", \
            "--auth-token", "$(MOCK_AUTH_TOKEN)", \
            "--admin-key", "$(MOCK_ADMIN_KEY)"]
```

## 6. Restart & Failure Recovery

Shared mock definitions are immutable artifacts read from disk or git repository checkouts. On process crash, reboot, or container restart:
1. The process restarts and loads identical rule definitions.
2. Initial scenario states default to `"Initial"`.
3. Health check probes (`GET /__admin/health`) return 200 OK once listening.
4. Test suites or integration harnesses can invoke `POST /__admin/state/reset` between test runs to ensure deterministic isolation.

