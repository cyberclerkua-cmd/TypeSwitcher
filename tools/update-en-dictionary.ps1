# Development-time acquisition of the pinned FrequencyWords English corpus.
# The application loads the bundled file; it never downloads dictionaries.
param([string]$CacheDirectory = (Join-Path $env:TEMP 'TypeSwitcher-downloads'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'dict'
if (-not (Test-Path -LiteralPath $target -PathType Container)) { throw 'Dictionary directory missing.' }
[IO.Directory]::CreateDirectory($CacheDirectory) | Out-Null
$source = Join-Path $CacheDirectory 'typeswitcher-en-50k.txt'
if (-not (Test-Path -LiteralPath $source)) {
    curl.exe --fail --location --silent --show-error --retry 2 --max-time 60 'https://raw.githubusercontent.com/hermitdave/FrequencyWords/f8a65e6ddc17e0baa2e366a909986798d8dbe55b/content/2018/en/en_50k.txt' --output $source
    if ($LASTEXITCODE -ne 0) { throw 'English corpus download failed.' }
}
if ((git hash-object --no-filters $source) -ne 'bbf5fa991058ff642732784b31af023f66462e1d') { throw 'English corpus checksum mismatch.' }
[IO.File]::Copy($source, (Join-Path $target 'en-50k.txt'), $true)
Get-FileHash -LiteralPath (Join-Path $target 'en-50k.txt') -Algorithm SHA256
