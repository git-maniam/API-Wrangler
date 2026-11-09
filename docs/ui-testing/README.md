# Desktop UI test execution guide

These are manual acceptance cases; see `execution-2026-11-09.md` for the partial execution attempt and blockers. Windows 11 x64, an interactive desktop and the build/commit under test are required. Record the build, tester, date, exact inputs, observed result, evidence and issue link in each ticket. Mark each positive and negative variant Pass, Fail, Blocked or Not run independently; do not close a ticket until both variants pass and evidence is reviewed. A failed or unavailable prerequisite blocks dependent variants, not a pass. The reported Open project issue is #95; reproduce it first and link dependent failures to it.

Use an isolated disposable working directory and local services only. From the repository root in PowerShell, create the project and start the fixture API in separate terminals:

```powershell
dotnet run --project src/ApiWrangler.Cli -c Release -- init --project "$env:TEMP\awr-ui-project" --name "UI QA"
dotnet run --project tests/Fixtures/ApiWrangler.FixtureApi -c Release
dotnet run --project src/ApiWrangler.Desktop -c Release
```

The fixture listens at `http://127.0.0.1:5057`: `/echo` reflects method, query, headers and body; `/delay` waits 10 seconds; `/redirect` redirects to `/echo`; `/large` streams 8 MiB. If a fixture port is occupied, stop the conflicting local process or mark dependent cases blocked. Do not send secrets or load to external hosts. Files in `data/` are synthetic. Copy them to a disposable location before editing. The project, fixture service and Git repositories must be reset between destructive variants. For editable assets, first use **Import & export** to preview and publish `data/postman-collection.json` and `data/postman-environment.json` into the disposable project; if project opening or publishing fails, record dependent cases as blocked.

In separate terminals, start the protocol fixture and generate volume/encoding variants (use a new empty directory):

```powershell
python docs/ui-testing/protocol_fixture.py
python docs/ui-testing/generate-data.py "$env:TEMP\awr-ui-data"
```

Protocol inputs are `data/sample.graphql` and `data/sample.proto`; endpoints are `http://127.0.0.1:5061/graphql`, `ws://127.0.0.1:5061/ws`, and `http://127.0.0.1:5061/events`. The deliberately minimal GraphQL fixture responds to the health query, WebSocket echoes a single-frame message, and SSE emits `fixture-ready` then closes. These fixtures were prepared for later use and have not been executed. JSON dataset files are `valid.json` and `valid.jsonl`; `valid.tsv`, encoding files and `large.csv` are generated above. For cancellation, increase the generator's 1,000-row count in a disposable copy if the operation finishes before cancellation can be observed.

For the TLS and cross-origin case, with OpenSSL installed, generate a disposable certificate and start the loopback service in separate terminals:

```powershell
New-Item -ItemType Directory -Path "$env:TEMP\awr-ui-tls" -Force | Out-Null
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$env:TEMP\awr-ui-tls\key.pem" -out "$env:TEMP\awr-ui-tls\cert.pem" -days 1 -subj /CN=localhost
python docs/ui-testing/tls_fixture.py "$env:TEMP\awr-ui-tls\cert.pem" "$env:TEMP\awr-ui-tls\key.pem"
```

Use `https://127.0.0.1:5062/ok` and `/redirect`, and check `http://127.0.0.1:5063/last` after the redirect. The certificate is intentionally untrusted; never install it in a trust store. Only synthetic credentials are permitted. If OpenSSL is unavailable, mark the TLS variant blocked and record the missing prerequisite.

For Git cases, make two disposable directories and local identity rather than touching the user's repository:

```powershell
git init --bare "$env:TEMP\awr-ui-remote.git"
git clone "$env:TEMP\awr-ui-remote.git" "$env:TEMP\awr-ui-git"
git -C "$env:TEMP\awr-ui-git" config user.name "UI QA"
git -C "$env:TEMP\awr-ui-git" config user.email "ui-qa@example.test"
```

Initialize a project inside the working clone, import assets as above and create a baseline commit before branch, restore or conflict scenarios. Construct divergent branch edits only within that clone. For GitHub Compare, a disposable GitHub repository or suitable synthetic remote is needed; opening the comparison page is a separate browser action, not a push.

For every case check that the UI stays responsive, errors are visible and actionable, no unexpected data is written, and no secret is exposed. Record screenshots with synthetic data only. At completion stop local listeners and delete only the disposable copies. Tickets begin in Backlog pending publication of this guide and availability of prerequisites; move actionable cases to Ready under the project workflow. The case catalog and GitHub links are in `cases.json`, with detailed variants in `cases-source.json` and the issues.

Scope is the controls exposed by `MainWindow.xaml`, not every CLI or backend feature. Manual Narrator acceptance remains tracked separately in #58. The gRPC UI labels its operation **Simulate Call**; it cannot establish live network gRPC acceptance. Record unavailable setup as blocked and do not substitute a simulated call for network verification.

See `coverage-matrix.md` for the mapping from visible tabs and actions to case IDs. Exhaustive coverage of all possible input combinations is impossible; this suite aims to cover every exposed action, option and major validation boundary. Review expectations against the current product specification and log discrepancies as defects, rather than adjusting results to fit observed behavior.
