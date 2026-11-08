param(
    [string]$SourcePng = "App-Icon.png",
    [string]$TargetIco = "src/ApiWrangler.Desktop/Resources/app-icon.ico"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repo = Split-Path $PSScriptRoot -Parent
$sourcePath = Join-Path $repo $SourcePng
$targetPath = Join-Path $repo $TargetIco

$targetDir = Split-Path $targetPath -Parent
if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
}

$source = [System.Drawing.Bitmap]::FromFile($sourcePath)
$sizes = @(16, 32, 48, 64, 128, 256)
$pngStreams = @()

foreach ($sz in $sizes) {
    $resized = New-Object System.Drawing.Bitmap $sz, $sz
    $g = [System.Drawing.Graphics]::FromImage($resized)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($source, 0, 0, $sz, $sz)
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $resized.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngStreams += ,@($sz, $ms.ToArray())
    $resized.Dispose()
}
$source.Dispose()

$fs = [System.IO.File]::Create($targetPath)
$bw = New-Object System.IO.BinaryWriter $fs

# ICO Header
$bw.Write([uint16]0) # Reserved
$bw.Write([uint16]1) # Type: 1 = ICO
$bw.Write([uint16]$sizes.Count) # Count

$offset = 6 + (16 * $sizes.Count)
foreach ($item in $pngStreams) {
    $sz = $item[0]
    $bytes = $item[1]
    $bWidth = if ($sz -eq 256) { [byte]0 } else { [byte]$sz }
    $bHeight = if ($sz -eq 256) { [byte]0 } else { [byte]$sz }
    $bw.Write($bWidth)
    $bw.Write($bHeight)
    $bw.Write([byte]0) # Color count
    $bw.Write([byte]0) # Reserved
    $bw.Write([uint16]1) # Color planes
    $bw.Write([uint16]32) # Bits per pixel
    $bw.Write([uint32]$bytes.Length) # Size in bytes
    $bw.Write([uint32]$offset) # Offset
    $offset += $bytes.Length
}

foreach ($item in $pngStreams) {
    $bw.Write($item[1])
}

$bw.Flush()
$fs.Dispose()

# Also copy source PNG to Resources directory for WPF runtime access
$resPng = Join-Path $targetDir "App-Icon.png"
Copy-Item $sourcePath $resPng -Force

Write-Output "Successfully generated icon: $targetPath ($((Get-Item $targetPath).Length) bytes)"

