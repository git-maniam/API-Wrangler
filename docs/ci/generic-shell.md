# Generic Shell & CI Pipeline Integration

This guide provides agnostic bash and PowerShell script examples for running API Wrangler in generic CI platforms (such as GitLab CI, Jenkins, TeamCity, or Azure Pipelines).

## Agnostic Bash Script (`run-tests.sh`)

```bash
#!/usr/bin/env bash
set -euo pipefail

PROJECT_DIR="${1:-./api-wrangler}"
COLLECTION_NAME="${2:-Regression}"
ARTIFACT_DIR="./ci-artifacts"

mkdir -p "${ARTIFACT_DIR}/reports" "${ARTIFACT_DIR}/runs"

echo "=== 1. Validating API Wrangler Project ==="
api-wrangler validate --project "${PROJECT_DIR}"

echo "=== 2. Executing Collection '${COLLECTION_NAME}' ==="
set +e
api-wrangler run \
  --project "${PROJECT_DIR}" \
  --collection "${COLLECTION_NAME}" \
  --reporters console,junit,html,json \
  --report-dir "${ARTIFACT_DIR}/reports" \
  --output "${ARTIFACT_DIR}/runs"
EXIT_CODE=$?
set -e

echo "=== 3. Execution Finished with Exit Code ${EXIT_CODE} ==="

case $EXIT_CODE in
  0)
    echo "SUCCESS: All assertions passed."
    exit 0
    ;;
  1)
    echo "FAILURE: One or more assertions failed. See reports in ${ARTIFACT_DIR}/reports."
    exit 1
    ;;
  2)
    echo "ERROR: Invalid configuration or project structure."
    exit 2
    ;;
  3)
    echo "ERROR: Execution infrastructure or transport error."
    exit 3
    ;;
  4)
    echo "INTERRUPTED: Run was cancelled."
    exit 4
    ;;
  *)
    echo "UNKNOWN ERROR: Exit code ${EXIT_CODE}."
    exit "${EXIT_CODE}"
    ;;
esac
```

## Agnostic PowerShell Script (`run-tests.ps1`)

```powershell
param(
    [string]$Project = "./api-wrangler",
    [string]$Collection = "Regression",
    [string]$Artifacts = "./ci-artifacts"
)

$ErrorActionPreference = "Stop"

New-Item -ItemType Directory -Force -Path "$Artifacts/reports", "$Artifacts/runs" | Out-Null

Write-Host "=== 1. Validating API Wrangler Project ==="
& api-wrangler validate --project $Project
if ($LASTEXITCODE -ne 0) {
    Write-Error "Project validation failed with code $LASTEXITCODE."
    exit $LASTEXITCODE
}

Write-Host "=== 2. Executing Collection '$Collection' ==="
& api-wrangler run `
    --project $Project `
    --collection $Collection `
    --reporters console,junit,html,json `
    --report-dir "$Artifacts/reports" `
    --output "$Artifacts/runs"

$runExit = $LASTEXITCODE
Write-Host "=== 3. Finished with Exit Code $runExit ==="
exit $runExit
```
