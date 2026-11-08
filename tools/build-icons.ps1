param(
    [string]$TemporaryRoot = $env:TEMP,
    [string]$Edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$assets = Join-Path $root 'assets'
if (-not (Test-Path -LiteralPath $assets)) { throw 'Assets directory is missing.' }
if (-not (Test-Path -LiteralPath $TemporaryRoot)) { throw 'Temporary parent directory is missing.' }
if (-not (Test-Path -LiteralPath $Edge)) { throw 'Microsoft Edge is required to render the vector source.' }
foreach ($name in @('typeswitcher', 'typeswitcher-action')) {
    $profile = Join-Path $TemporaryRoot ('typeswitcher-render-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($profile) | Out-Null
    $source = [Uri]::new((Join-Path $assets ($name + '.svg'))).AbsoluteUri
    $target = Join-Path $assets ($name + '.png')
    $arguments = @('--headless', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--no-default-browser-check', '--disable-background-networking', '--disable-sync', '--force-device-scale-factor=1', '--default-background-color=00000000', '--virtual-time-budget=1000', '--window-size=256,256', "--user-data-dir=`"$profile`"", "--screenshot=`"$target`"", "`"$source`"")
    $process = Start-Process -FilePath $Edge -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw "Render timed out: $name" }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $target)) { throw "Render failed: $name" }
    try { [IO.Directory]::Delete($profile, $true) } catch { }
}
Add-Type -AssemblyName System.Drawing
$sourceImage = [Drawing.Image]::FromFile((Join-Path $assets 'typeswitcher.png'))
$frames = @()
try {
    foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($sourceImage, [Drawing.Rectangle]::new(0, 0, $size, $size))
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            $frames += @{ Size = $size; Bytes = $stream.ToArray() }
        }
        finally { $graphics.Dispose(); $bitmap.Dispose(); $stream.Dispose() }
    }
}
finally { $sourceImage.Dispose() }
$file = [IO.File]::Create((Join-Path $assets 'typeswitcher.ico'))
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $sizeByte = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$sizeByte); $writer.Write([byte]$sizeByte)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
}
finally { $writer.Dispose(); $file.Dispose() }
'Created typeswitcher.ico (16/20/24/32/40/48/64/128/256) and tray PNG states.'
