param(
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [Parameter(Mandatory = $true)][string]$DestinationDirectory
)
$ErrorActionPreference = 'Stop'
$source = [IO.Path]::GetFullPath($SourceDirectory)
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$sourceData = Join-Path $source 'data'
$destinationData = Join-Path $destination 'data'
if (-not (Test-Path -LiteralPath (Join-Path $sourceData 'config.json'))) { throw 'Source config.json is missing.' }
if (-not (Test-Path -LiteralPath $destination)) { throw 'Destination application directory is missing.' }
if (Test-Path -LiteralPath $destinationData) { throw 'Destination data already exists; automatic overwrite is not allowed.' }
$staging = Join-Path $destination ('.data-migration-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($staging) | Out-Null
try {
    robocopy $sourceData $staging /E /XJ /R:0 /W:0 /NFL /NDL /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw 'Could not copy application data.' }
    foreach ($file in [IO.Directory]::EnumerateFiles($sourceData, '*', [IO.SearchOption]::AllDirectories)) {
        $relative = $file.Substring($sourceData.Length + 1)
        $copy = Join-Path $staging $relative
        if (-not (Test-Path -LiteralPath $copy)) { throw "Missing migrated file: $relative" }
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $copy -Algorithm SHA256).Hash) { throw "Migration checksum mismatch: $relative" }
    }
    $executables = @([IO.Directory]::GetFiles($source, '*.exe', [IO.SearchOption]::TopDirectoryOnly))
    $history = @{ previous_executables = $executables; migrated_at = [DateTime]::UtcNow.ToString('o') }
    [IO.File]::WriteAllText((Join-Path $staging 'upgrade-history.json'), ($history | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    [IO.Directory]::Move($staging, $destinationData)
    "Data migrated and verified: $destinationData"
}
finally { if (Test-Path -LiteralPath $staging) { [IO.Directory]::Delete($staging, $true) } }
