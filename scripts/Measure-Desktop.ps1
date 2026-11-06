param([int]$Samples = 10, [string]$OutputDirectory = 'artifacts/desktop-baseline')
$ErrorActionPreference = 'Stop'
if ($Samples -lt 1 -or $Samples -gt 100) { throw 'Samples must be between 1 and 100.' }
$repo = Split-Path $PSScriptRoot -Parent
$executable = Join-Path $repo 'src/ApiWrangler.Desktop/bin/Release/net10.0-windows/ApiWrangler.Desktop.exe'
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build the Release solution first.' }
$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
New-Item -ItemType Directory -Force $output | Out-Null
$samplesTaken = @()
for ($i = 0; $i -lt $Samples; $i++) {
    $samplePath = Join-Path $output "sample-$i"
    $process = Start-Process -FilePath $executable -ArgumentList @('--diagnostics', ('"' + $samplePath + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Desktop diagnostics timed out.' }
    if ($process.ExitCode -ne 0) { throw "Desktop diagnostics failed; inspect $samplePath/error.txt" }
    $samplesTaken += Get-Content -Raw (Join-Path $samplePath 'metrics.json') | ConvertFrom-Json
}
$sortedStarts = @($samplesTaken.startupMs | Sort-Object)
$sortedGrid = @($samplesTaken.datasetMs | Sort-Object)
$index = [Math]::Ceiling($Samples * 0.95) - 1
$summary = [ordered]@{
    measuredAtUtc = [DateTime]::UtcNow.ToString('o')
    samples = $Samples
    startupP95Ms = $sortedStarts[$index]
    datasetP95Ms = $sortedGrid[$index]
    maxIdlePrivateBytes = ($samplesTaken.privateBytes | Measure-Object -Maximum).Maximum
    maxRealizedRows = ($samplesTaken.realizedRows | Measure-Object -Maximum).Maximum
    maxCachedRows = ($samplesTaken.cachedRows | Measure-Object -Maximum).Maximum
    hardware = (Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores)
    memoryBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    note = 'Warm process-start feasibility baseline. No cache flushing; no project index exists yet. Not full NFR acceptance.'
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $output 'summary.json')
$summary | ConvertTo-Json -Depth 5
