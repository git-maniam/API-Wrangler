param([ValidateSet('win-x64')][string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repo "artifacts/publish/$Runtime"
dotnet publish (Join-Path $repo 'src/ApiWrangler.Desktop') -c Release -r $Runtime --self-contained true -p:RestoreLockedMode=true -p:PublishTrimmed=false -p:PublishAot=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $output
Copy-Item -LiteralPath (Join-Path $repo 'docs/dependencies.md') -Destination $output
New-Item -ItemType Directory -Path (Join-Path $output 'docs') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'docs/help.html') -Destination (Join-Path $output 'docs/help.html') -Force
Compress-Archive -Path (Join-Path $output '*') -DestinationPath (Join-Path $repo "artifacts/ApiWrangler-$Runtime-preview.zip") -Force
Get-FileHash (Join-Path $repo "artifacts/ApiWrangler-$Runtime-preview.zip") -Algorithm SHA256
