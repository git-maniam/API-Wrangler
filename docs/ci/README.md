# API Wrangler CI & Automation Guide

API Wrangler provides a dedicated, headless command-line interface (`api-wrangler` / `awr`) designed for deterministic continuous integration (CI) environments without requiring desktop UI components or windowing systems.

## CLI Contract & Rules

1. **Noninteractive execution:** Commands run to completion without waiting for human input or prompts.
2. **Headless:** Operates on Windows and Linux x64 without desktop UI dependencies (e.g., no X11 or WPF required).
3. **Secret hygiene:** Secrets are referenced via `{{secret:KEY}}` and resolved through environment variables (`API_WRANGLER_SECRET_<KEY>` or `<KEY>`) or local protected stores. Raw secrets are never printed to stdout or committed to Git.
4. **Structured results:** Test outcomes can be emitted simultaneously to multiple formats: `console`, `junit`, `json`, `markdown`, and `html`.
5. **Deterministic exit codes:**
   - `0`: Execution passed
   - `1`: Assertions failed
   - `2`: Invalid configuration or project
   - `3`: Execution infrastructure error (transport/network failure)
   - `4`: Cancelled or interrupted
   - `5`: Required dependency unavailable (e.g. Git or Node probe)
6. **No automatic push:** The CLI never automatically pushes to remote repositories.
7. **Artifact retention:** CI systems must retain failure evidence and reports even when assertions fail (`if: always()`).

## CLI Commands Reference

### Initialize Project
```bash
api-wrangler init --project ./api-wrangler --name "Regression Suite"
```

### Validate Project
```bash
api-wrangler validate --project ./api-wrangler
```

### Run Collections
```bash
api-wrangler run \
  --project ./api-wrangler \
  --collection checkout \
  --environment staging \
  --reporters console,junit,json,html,markdown \
  --report-dir ./artifacts/reports \
  --output ./artifacts/runs
```

## CI Platform Guides

- [GitHub Actions Integration](github-actions.md)
- [Generic Shell / Jenkins / GitLab CI](generic-shell.md)
- [Windows Self-Hosted Runner](windows-runner.md)
- [Linux Container Runner](linux-runner.md)

