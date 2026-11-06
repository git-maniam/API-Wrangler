$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo 'artifacts/desktop-smoke'
New-Item -ItemType Directory -Force $output | Out-Null
$fixturePath = Join-Path $repo 'tests/Fixtures/ApiWrangler.FixtureApi/bin/Release/net10.0/ApiWrangler.FixtureApi.exe'
$desktopPath = Join-Path $repo 'src/ApiWrangler.Desktop/bin/Release/net10.0-windows/ApiWrangler.Desktop.exe'
if (Get-NetTCPConnection -LocalPort 5057 -State Listen -ErrorAction SilentlyContinue) { throw 'Port 5057 is occupied. Stop that fixture before this test.' }
$fixture = Start-Process -FilePath $fixturePath -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output 'fixture.stdout.log') -RedirectStandardError (Join-Path $output 'fixture.stderr.log')
try {
    $ready = $false
    for ($i = 0; $i -lt 50; $i++) {
        try { Invoke-RestMethod 'http://127.0.0.1:5057/echo' | Out-Null; $ready = $true; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'Local HTTP fixture did not become ready.' }
    $desktop = Start-Process -FilePath $desktopPath -ArgumentList @('--http-smoke', ('"' + $output + '"')) -WindowStyle Hidden -PassThru
    if (-not $desktop.WaitForExit(30000)) { $desktop.Kill(); throw 'Desktop HTTP smoke test timed out.' }
    if ($desktop.ExitCode -ne 0) { throw "Desktop smoke failed; inspect $output/error.txt" }
    Get-Content -Raw (Join-Path $output 'http-smoke.json')
} finally {
    if (-not $fixture.HasExited) { $fixture.Kill(); $fixture.WaitForExit() }
}
