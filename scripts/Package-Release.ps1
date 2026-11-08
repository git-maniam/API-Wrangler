param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
$packagesDir = Join-Path $repo "artifacts/packages"

if (-not (Test-Path $packagesDir)) {
    New-Item -ItemType Directory -Path $packagesDir -Force | Out-Null
}

Write-Output "=== Packaging API Wrangler Release v$Version ==="

# 1. Desktop Windows x64 (self-contained)
$desktopPublishDir = Join-Path $repo "artifacts/publish/win-x64-desktop"
Write-Output "Publishing Desktop (win-x64)..."
dotnet publish (Join-Path $repo "src/ApiWrangler.Desktop") -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishAot=false -p:RestoreLockedMode=true -o $desktopPublishDir
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed." }

Copy-Item (Join-Path $repo "LICENSE") $desktopPublishDir -Force
Copy-Item (Join-Path $repo "docs/dependencies.md") $desktopPublishDir -Force
$desktopZip = Join-Path $packagesDir "api-wrangler-desktop-v$Version-win-x64.zip"
if (Test-Path $desktopZip) { Remove-Item $desktopZip -Force }
Compress-Archive -Path (Join-Path $desktopPublishDir "*") -DestinationPath $desktopZip -Force
Write-Output "Created: $desktopZip"

# 2. CLI Windows x64
$cliWinPublishDir = Join-Path $repo "artifacts/publish/win-x64-cli"
Write-Output "Publishing CLI (win-x64)..."
dotnet publish (Join-Path $repo "src/ApiWrangler.Cli") -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:PublishAot=false -p:RestoreLockedMode=true -o $cliWinPublishDir
if ($LASTEXITCODE -ne 0) { throw "CLI win-x64 publish failed." }

Copy-Item (Join-Path $repo "LICENSE") $cliWinPublishDir -Force
$cliWinZip = Join-Path $packagesDir "api-wrangler-cli-v$Version-win-x64.zip"
if (Test-Path $cliWinZip) { Remove-Item $cliWinZip -Force }
Compress-Archive -Path (Join-Path $cliWinPublishDir "*") -DestinationPath $cliWinZip -Force
Write-Output "Created: $cliWinZip"

# 3. CLI Linux x64
$cliLinuxPublishDir = Join-Path $repo "artifacts/publish/linux-x64-cli"
Write-Output "Publishing CLI (linux-x64)..."
dotnet publish (Join-Path $repo "src/ApiWrangler.Cli") -c Release -r linux-x64 --self-contained true -p:PublishTrimmed=false -p:PublishAot=false -p:RestoreLockedMode=true -o $cliLinuxPublishDir
if ($LASTEXITCODE -ne 0) { throw "CLI linux-x64 publish failed." }

Copy-Item (Join-Path $repo "LICENSE") $cliLinuxPublishDir -Force
$cliLinuxZip = Join-Path $packagesDir "api-wrangler-cli-v$Version-linux-x64.zip"
if (Test-Path $cliLinuxZip) { Remove-Item $cliLinuxZip -Force }
Compress-Archive -Path (Join-Path $cliLinuxPublishDir "*") -DestinationPath $cliLinuxZip -Force
Write-Output "Created: $cliLinuxZip"

# 4. Generate SHA256SUMS.txt
$sumsPath = Join-Path $packagesDir "SHA256SUMS.txt"
$zipFiles = Get-ChildItem -Path $packagesDir -Filter "*.zip"
$sumsContent = @()
foreach ($file in $zipFiles) {
    $hash = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $sumsContent += "$hash  $($file.Name)"
}
Set-Content -Path $sumsPath -Value ($sumsContent -join "`n") -Encoding ASCII
Write-Output "Generated SHA256 checksums at: $sumsPath"

# 5. Generate SBOM (CycloneDX / SPDX JSON format)
$sbomPath = Join-Path $packagesDir "sbom.json"
$sbom = [ordered]@{
    bomFormat = "CycloneDX"
    specVersion = "1.5"
    serialNumber = "urn:uuid:$([Guid]::NewGuid())"
    version = 1
    metadata = @{
        timestamp = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
        component = @{
            name = "api-wrangler"
            version = $Version
            type = "application"
            description = "API Wrangler — Git-backed API testing workbench"
            licenses = @(
                @{ license = @{ id = "MIT" } }
            )
        }
    }
    components = @(
        @{ name = "CommunityToolkit.Mvvm"; version = "8.4.0"; type = "library"; license = "MIT" },
        @{ name = "AvalonEdit"; version = "6.3.1.115"; type = "library"; license = "MIT" },
        @{ name = "Microsoft.Extensions.Hosting"; version = "10.0.0"; type = "library"; license = "MIT" },
        @{ name = "System.CommandLine"; version = "2.0.0-beta4.22272.1"; type = "library"; license = "MIT" },
        @{ name = "Microsoft.Data.Sqlite"; version = "10.0.0"; type = "library"; license = "MIT" }
    )
}
$sbomJson = $sbom | ConvertTo-Json -Depth 6
Set-Content -Path $sbomPath -Value $sbomJson -Encoding UTF8
Write-Output "Generated SBOM at: $sbomPath"

# 6. Generate Third Party Notices
$noticesPath = Join-Path $packagesDir "THIRD_PARTY_NOTICES.md"
$noticesContent = @"
# Third-Party Software Notices and Information

This package includes third-party software subject to open source licenses.

## Components

### CommunityToolkit.Mvvm
- Version: 8.4.0
- License: MIT
- Copyright: .NET Foundation and Contributors

### AvalonEdit
- Version: 6.3.1.115
- License: MIT
- Copyright: Daniel Grunwald, AlphaSierraPapa

### Microsoft .NET Runtime and Extensions
- Version: 10.0
- License: MIT
- Copyright: .NET Foundation and Contributors

---
Licensed under the Apache License 2.0 or MIT License as noted above.
"@
Set-Content -Path $noticesPath -Value $noticesContent -Encoding UTF8
Write-Output "Generated Third-Party Notices at: $noticesPath"

Write-Output "=== Packaging completed successfully ==="

