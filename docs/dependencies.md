# Dependency inventory

The foundation uses exact NuGet versions in `Directory.Packages.props`, per-project lockfiles, and an npm lockfile. Dependency installation is explicit. No installation occurs when the desktop opens.

| Dependency | Pinned version | License | Purpose |
|---|---|---|---|
| .NET SDK | 10.0.403 | MIT and included third-party notices | Build toolchain |
| .NET desktop/runtime | 10.0.14 on the verified host | MIT and included third-party notices | Execution |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | View models and cancellable commands |
| AvalonEdit | 6.3.1.120 | MIT | Native text editing |
| Microsoft.Extensions.Hosting and its dependency family | 10.0.14 | MIT | Composition and lifetime |
| System.CommandLine | 2.0.14 | MIT | CLI |
| Microsoft.NET.Test.Sdk and test platform | 18.11.0 | MIT | Tests |
| xUnit | 2.9.3 | Apache-2.0 | Tests |
| xunit.runner.visualstudio | 4.0.1 | Apache-2.0 | Test discovery |
| Node.js | 26.13.0 | MIT and bundled third-party notices | Optional explicit worker probe |
| TypeScript | 5.9.3 | Apache-2.0 | Worker compilation |
| @types/node / undici-types | 26.0.0 / locked transitive version | MIT | Build-time typings |

NuGet package license expressions were inspected locally, including transitive dependencies. `xunit.abstractions` 2.0.3 uses legacy license-URL metadata pointing to the xUnit license rather than an SPDX expression. No paid component has been added. Node is not redistributed in application packages.

Before a public release, collect full third-party license texts and runtime notices into the installer, generate the release SBOM, and repeat the vulnerability/license review. Preview ZIPs are development artifacts, not signed release packages.

Regenerate the package inventory with `dotnet list ApiWrangler.slnx package --include-transitive --format json`. Check npm dependencies with `npm audit` in `workers/node`. The initial npm audit reported no vulnerabilities.
