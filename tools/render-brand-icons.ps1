param(
    [string]$TemporaryRoot = $env:TEMP,
    [string]$Edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'docs\branding\typeswitcher'
if (-not (Test-Path -LiteralPath $output)) { throw 'The branding directory is missing.' }
if (-not (Test-Path -LiteralPath $TemporaryRoot)) { throw 'The temporary parent directory is missing.' }
if (-not (Test-Path -LiteralPath $Edge)) { throw 'Microsoft Edge is not available.' }
$targets = @(
    @{ Input = '01-keycap.svg'; Output = '01-keycap.png'; Width = 256; Height = 256 },
    @{ Input = '02-switch.svg'; Output = '02-switch.png'; Width = 256; Height = 256 },
    @{ Input = '03-orbit.svg'; Output = '03-orbit.png'; Width = 256; Height = 256 },
    @{ Input = 'preview.html'; Output = 'preview.png'; Width = 1440; Height = 980 }
)
foreach ($target in $targets) {
    $profile = Join-Path $TemporaryRoot ('typeswitcher-icon-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($profile) | Out-Null
    $source = [Uri]::new((Join-Path $output $target.Input)).AbsoluteUri
    $destination = Join-Path $output $target.Output
    $arguments = @(
        '--headless', '--disable-gpu', '--hide-scrollbars', '--no-first-run',
        '--no-default-browser-check', '--disable-background-networking', '--disable-sync',
        '--force-device-scale-factor=1', '--default-background-color=00000000',
        '--virtual-time-budget=1200', "--window-size=$($target.Width),$($target.Height)",
        "--user-data-dir=`"$profile`"", "--screenshot=`"$destination`"", "`"$source`""
    )
    $process = Start-Process -FilePath $Edge -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(45000)) { $process.Kill(); throw "Render timed out: $($target.Input)" }
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $destination)) { throw "Render failed: $($target.Input)" }
    "Rendered: $destination"
    try { [IO.Directory]::Delete($profile, $true) } catch { }
}
