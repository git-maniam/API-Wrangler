# Foundation architecture

The desktop and CLI use the same `HttpRequestService` and `HttpTransport`. The Domain assembly contains only request/result values. Application owns validation and the transport port. Execution implements networking and the temporary Git/Node feasibility probes. Desktop owns WPF and MVVM. No WPF references flow into Domain, Application, or Execution; an automated test enforces this boundary.

HTTP work is dispatched away from the WPF dispatcher. The transport uses asynchronous streaming, a deadline linked to user cancellation, bounded preview memory, and an explicit saved-body limit. TLS validation uses system defaults. Redirects are returned to the user without following them. Cookies and implicit retries are not implemented. HTTP 500 is a completed transport, not an assertion result; assertions are a later phase.

Raw response bytes are local operational artifacts under the user's application-data directory. CLI stdout contains only transport metadata. There is no secret store, retention scheduler, project format, or sanitized-sharing feature yet; use synthetic/local fixtures for this foundation preview.

The paged dataset is a deterministic, read-only feasibility fixture. It retains at most four 256-row pages and uses recycling row virtualization. It is not an implementation of dataset import or editing.

The Node probe uses inherited OS pipes, a four-byte big-endian length prefix, JSON protocol version 1, request correlation, a 64 KiB frame limit, and a ten-second deadline. Output pipes are reserved for the protocol; stderr is drained without retaining contents. Cancellation kills the worker process tree. Production scripting, Windows Job Objects for crash containment, trust records, packages, and compatibility adapters are still required in phase 6. The worker is not a security sandbox.

Git is invoked with `ProcessStartInfo.ArgumentList` and inherited host configuration. Status and explicit fetch are available. Fetch never invokes merge/rebase or changes working files. The CLI emits exit status rather than raw diagnostics, which may include sensitive remote URLs. Git mutations and conflict workflows are not implemented.

Future persistence, protocol, service, and integration modules follow the specification. They are intentionally not represented by empty projects or success-returning implementations.
