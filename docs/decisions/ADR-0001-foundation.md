# ADR-0001: Native WPF shell and shared headless execution

Status: accepted for the foundation implementation.

## Decision

Use .NET 10, WPF with the system Fluent theme, CommunityToolkit.Mvvm, and AvalonEdit. Keep Domain and Application independent of WPF. Share the HTTP transport with the CLI. Target Windows 11 x64 desktop releases and retain Linux x64 headless compatibility. The user explicitly deferred ARM64 on 2026-11-06; it is not a current release gate.

Use explicit process adapters for installed Git and optional Node. Their initial feasibility implementations live in Execution; extract separate modules when production boundaries justify them. Do not scaffold unrelated future services.

## Alternatives and consequences

A browser shell would violate the implementation baseline and increase process/resource requirements. Separate desktop and CLI engines would introduce execution drift. A custom text editor would duplicate a mature component. None is adopted.

Retaining the full Generic Host provides a familiar composition root. The measured foundation shell is below the initial memory target. Node starts only through an explicit probe command. Ordinary HTTP use needs neither Node nor Git.

## Temporary limitations

The transport does not follow redirects, decode arbitrary encodings, manage cookie jars, or implement retries, authentication flows, or assertions. Preview text is UTF-8; stored bytes are unchanged. These limitations are visible and do not return fabricated capability success.

The extra `send`, `git-probe`, and `node-probe` CLI commands support phase-0 verification. They do not replace the final `init`, `validate`, `run`, and other commands in specification section 28.

Signed installer distribution and full project benchmarks remain release gates. If ARM64 is reintroduced later, cross-compilation alone will not satisfy native validation.
