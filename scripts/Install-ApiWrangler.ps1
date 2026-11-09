param(
    [string]$SourceDirectory = "",
    [string]$InstallDirectory = "$env:LOCALAPPDATA\Programs\ApiWrangler",
    [switch]$NoDesktopShortcut = $false
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $repo = Split-Path $PSScriptRoot -Parent
    $SourceDirectory = Join-Path $repo "artifacts/publish/win-x64"
    if (-not (Test-Path $SourceDirectory)) {
        $SourceDirectory = Join-Path $repo "src/ApiWrangler.Desktop/bin/Release/net10.0-windows"
    }
}

if (-not (Test-Path $SourceDirectory)) {
    throw "Source directory does not exist: $SourceDirectory. Please publish the desktop application first."
}

Write-Output "Installing API Wrangler to: $InstallDirectory"
if (-not (Test-Path $InstallDirectory)) {
    New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
}

# Copy application binaries
Copy-Item (Join-Path $SourceDirectory "*") $InstallDirectory -Recurse -Force

$exePath = Join-Path $InstallDirectory "ApiWrangler.Desktop.exe"
if (-not (Test-Path $exePath)) {
    $exePath = Join-Path $InstallDirectory "ApiWrangler.Desktop.dll"
}

# Create shortcuts via WScript.Shell
$wsh = New-Object -ComObject WScript.Shell

# 1. Start Menu Shortcut
$startMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
if (Test-Path $startMenuDir) {
    $startShortcutPath = Join-Path $startMenuDir "API Wrangler.lnk"
    $startShortcut = $wsh.CreateShortcut($startShortcutPath)
    $startShortcut.TargetPath = $exePath
    $startShortcut.WorkingDirectory = $InstallDirectory
    $startShortcut.Description = "API Wrangler - Git-backed API Workbench"
    $startShortcut.IconLocation = "$exePath,0"
    $startShortcut.Save()
    Write-Output "Created Start Menu shortcut: $startShortcutPath"
}

# 2. Desktop Shortcut
if (-not $NoDesktopShortcut) {
    $desktopDir = [Environment]::GetFolderPath("Desktop")
    if (Test-Path $desktopDir) {
        $desktopShortcutPath = Join-Path $desktopDir "API Wrangler.lnk"
        $desktopShortcut = $wsh.CreateShortcut($desktopShortcutPath)
        $desktopShortcut.TargetPath = $exePath
        $desktopShortcut.WorkingDirectory = $InstallDirectory
        $desktopShortcut.Description = "API Wrangler - Git-backed API Workbench"
        $desktopShortcut.IconLocation = "$exePath,0"
        $desktopShortcut.Save()
        Write-Output "Created Desktop shortcut: $desktopShortcutPath"
    }
}

# 3. Create Uninstaller script in install directory
$uninstallerPath = Join-Path $InstallDirectory "Uninstall-ApiWrangler.ps1"
$uninstallerContent = @"
`$ErrorActionPreference = 'Stop'
Write-Output 'Uninstalling API Wrangler...'

# Remove shortcuts
`$startLnk = Join-Path `$env:APPDATA 'Microsoft\Windows\Start Menu\Programs\API Wrangler.lnk'
if (Test-Path `$startLnk) { Remove-Item `$startLnk -Force }

`$deskLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'API Wrangler.lnk'
if (Test-Path `$deskLnk) { Remove-Item `$deskLnk -Force }

# Note: User data under %LOCALAPPDATA%\ApiWrangler is strictly preserved!
Write-Output 'User data under `$env:LOCALAPPDATA\ApiWrangler was preserved.'
Write-Output 'Removing binaries from $InstallDirectory...'
Remove-Item '$InstallDirectory' -Recurse -Force -ErrorAction SilentlyContinue
Write-Output 'Uninstallation complete.'
"@
Set-Content -Path $uninstallerPath -Value $uninstallerContent -Encoding UTF8

Write-Output "API Wrangler installed successfully. User data under $env:LOCALAPPDATA\ApiWrangler remains isolated and preserved."

