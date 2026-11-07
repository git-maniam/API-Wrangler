# Windows Self-Hosted Runner Integration

API Wrangler CLI is built for Windows 11 x64 and Windows Server 2022/2025. It supports both local Windows DPAPI secret encryption and standard environment-injected secrets.

## Key Considerations for Windows Runners

1. **Noninteractive Service Accounts:**
   When running under a service account without an interactive desktop session, Windows DPAPI user-scope keys may not be available. Pass secrets directly via environment variables (`API_WRANGLER_SECRET_<KEY>` or `<KEY>`) or configure a machine-level certificate store.
2. **Headless Execution:**
   The `api-wrangler` CLI operates headlessly without starting WPF or needing an active desktop display.

## Example GitHub Actions or Azure DevOps Runner Config

```powershell
# In Windows Runner PowerShell script step:
$env:API_WRANGLER_SECRET_AUTH_KEY = "my-secure-key"

& dotnet run --project src/ApiWrangler.Cli -c Release -- run `
    --project ./api-wrangler `
    --collection "Payment Service" `
    --reporters console,junit,html `
    --report-dir ./artifacts/reports `
    --output ./artifacts/runs
```
