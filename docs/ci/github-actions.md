# GitHub Actions Integration

## Overview

API Wrangler integrates cleanly with GitHub Actions. It supports automated validation, secret injection from repository secrets, JUnit test report publishing, and HTML/JSON artifact archiving.

## Security Best Practices

> [!WARNING]
> **Never run untrusted pull-request scripts with privileged repository secrets.**
> Do not recommend or use `pull_request_target` as a shortcut for executing arbitrary contributed tests. Use `pull_request` triggers with limited permissions for fork contributions, and keep privileged integration test runs restricted to `main` branch or protected environments.

> [!IMPORTANT]
> **Always retain artifacts on failure.**
> Use `if: always()` on the artifact upload step so that failures generate full debuggable evidence (JSON traces, JUnit reports, HTML summaries).

## Workflow Example

Create `.github/workflows/api-regression.yml`:

```yaml
name: API Regression Suite

on:
  push:
    branches: [ main ]
  pull_request:
    branches: [ main ]

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - name: Checkout repository
        uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Build API Wrangler CLI
        run: dotnet build src/ApiWrangler.Cli -c Release

      - name: Validate API Wrangler Project
        run: dotnet run --project src/ApiWrangler.Cli -c Release -- validate --project ./api-wrangler

      - name: Run API Collections
        env:
          API_WRANGLER_SECRET_AUTH_TOKEN: ${{ secrets.STAGING_AUTH_TOKEN }}
          API_WRANGLER_SECRET_API_KEY: ${{ secrets.STAGING_API_KEY }}
        run: |
          dotnet run --project src/ApiWrangler.Cli -c Release -- run \
            --project ./api-wrangler \
            --collection "Checkout Regression" \
            --environment "staging" \
            --reporters console,junit,html,json \
            --report-dir ./test-artifacts/reports \
            --output ./test-artifacts/runs

      - name: Publish Test Report
        uses: dorny/test-reporter@v1
        if: always()
        with:
          name: API Wrangler Test Results
          path: ./test-artifacts/reports/junit.xml
          reporter: java-junit

      - name: Archive Test Evidence
        uses: actions/upload-artifact@v4
        if: always()
        with:
          name: api-wrangler-evidence
          path: ./test-artifacts/
          retention-days: 14
```
