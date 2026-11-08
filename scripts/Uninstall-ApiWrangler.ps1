param(
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\ApiWrangler"
)

$ErrorActionPreference = "Stop"
Write-Output "Uninstalling API Wrangler..."

# 1. Remove shortcuts
$startLnk = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\API Wrangler.lnk"
if (Test-Path $startLnk) {
    Remove-Item $startLnk -Force
    Write-Output "Removed Start Menu shortcut."
}

$deskLnk = Join-Path ([Environment]::GetFolderPath("Desktop")) "API Wrangler.lnk"
if (Test-Path $deskLnk) {
    Remove-Item $deskLnk -Force
    Write-Output "Removed Desktop shortcut."
}

# 2. Strict User Data Preservation Guarantee:
# User projects, SQLite storage, settings, and logs under %LOCALAPPDATA%\ApiWrangler are NOT deleted.
$userDataDir = Join-Path $env:LOCALAPPDATA "ApiWrangler"
if (Test-Path $userDataDir) {
    Write-Output "PRESERVED: User data directory at '$userDataDir' was left intact."
}

# 3. Remove application binaries
if (Test-Path $InstallDirectory) {
    Remove-Item $InstallDirectory -Recurse -Force
    Write-Output "Removed application binary directory: $InstallDirectory"
}

Write-Output "API Wrangler uninstalled cleanly. User data remains safely preserved."

