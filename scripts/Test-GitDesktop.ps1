$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo 'artifacts/git-desktop-smoke'
$desktopPath = Join-Path $repo 'src/ApiWrangler.Desktop/bin/Release/net10.0-windows/ApiWrangler.Desktop.exe'
New-Item -ItemType Directory -Force $output | Out-Null
if (-not (Test-Path -LiteralPath $desktopPath)) { throw 'Build the Release desktop project before running the Git workbench smoke.' }
$desktop = Start-Process -FilePath $desktopPath -ArgumentList @('--git-smoke', ('"' + $output + '"')) -WindowStyle Hidden -PassThru
if (-not $desktop.WaitForExit(60000)) { $desktop.Kill(); throw 'Desktop Git workbench smoke timed out.' }
if ($desktop.ExitCode -ne 0) { throw "Desktop Git workbench smoke failed; inspect $(Join-Path $output 'error.txt')" }
Get-Content -Raw (Join-Path $output 'git-smoke.json')
