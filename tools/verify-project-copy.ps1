param(
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [Parameter(Mandatory = $true)][string]$TargetRoot
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceRoot)
$target = [IO.Path]::GetFullPath($TargetRoot)
$excluded = @('.git', 'bin', 'obj', 'dist', 'artifacts', 'TestResults')
function Files([string]$directory) {
    foreach ($file in [IO.Directory]::EnumerateFiles($directory)) { $file }
    foreach ($child in [IO.Directory]::EnumerateDirectories($directory)) {
        if ($excluded -notcontains [IO.Path]::GetFileName($child)) { Files $child }
    }
}
$count = 0
foreach ($file in Files $source) {
    $relative = $file.Substring($source.Length + 1)
    $copy = Join-Path $target $relative
    if (-not (Test-Path -LiteralPath $copy)) { throw "Missing file: $relative" }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash) { throw "Checksum mismatch: $relative" }
    $count++
}
"Project copy verified: $count files."
