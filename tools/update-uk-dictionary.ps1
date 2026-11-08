# Reproducible upstream data acquisition. Only the pinned archive is accepted.
param([switch]$List, [string]$CacheDirectory = (Join-Path $env:TEMP 'TypeSwitcher-downloads'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cache = $CacheDirectory
$target = Join-Path $root 'dict\hunspell'
if (-not (Test-Path -LiteralPath $cache)) { [IO.Directory]::CreateDirectory($cache) | Out-Null }
if (-not (Test-Path -LiteralPath $target)) { throw "Missing dictionary directory: $target" }
$archive = Join-Path $cache 'hunspell-uk_UA_6.8.6_nolat.zip'
$url = 'https://github.com/brown-uk/dict_uk/releases/download/v6.8.6/hunspell-uk_UA_6.8.6_nolat.zip'
$sha256 = '7648eb09e6a7d442090bdd2a396898facef048456e316d4c4df5beb19becd4aa'
if (-not (Test-Path -LiteralPath $archive)) {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sha256) {
    throw 'Upstream archive checksum mismatch.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    if ($List) { $zip.Entries.FullName; return }
    foreach ($name in @('uk_UA.dic', 'uk_UA.aff')) {
        $entries = @($zip.Entries | Where-Object { $_.Name -eq $name })
        if ($entries.Count -ne 1 -or $entries[0].Length -gt 67108864) { throw "Invalid entry: $name" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], (Join-Path $target $name), $true)
    }
    $licenses = @($zip.Entries | Where-Object { $_.Name -match '(?i)(license|readme|copying)' -and $_.Length -gt 0 })
    $text = "VESUM Hunspell 6.8.6 (nolat)`nCopyright Andriy Rysin, Vasyl Starko and the BrUK team.`nSource: $url`nArchive SHA256: $sha256`nHunspell distribution license: MPL 1.1.`nSource and license declaration: https://github.com/brown-uk/dict_uk/tree/v6.8.6/distr/hunspell`n`n"
    foreach ($entry in $licenses) {
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $text += "--- $($entry.Name) ---`n" + $reader.ReadToEnd() + "`n" }
        finally { $reader.Dispose() }
    }
    $mpl = Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/spdx/license-list-data/main/text/MPL-1.1.txt'
    $text += $mpl.Content
    [IO.File]::WriteAllText((Join-Path $target 'uk_UA.LICENSE'), $text, [Text.UTF8Encoding]::new($false))
    $frequency = Join-Path $cache 'typeswitcher-uk-50k.txt'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://raw.githubusercontent.com/hermitdave/FrequencyWords/f8a65e6ddc17e0baa2e366a909986798d8dbe55b/content/2018/uk/uk_50k.txt' -OutFile $frequency
    if ((git hash-object --no-filters $frequency) -ne 'b5c607e7fe06c4ea1f3456a1a41f35f005b0a236') { throw 'Frequency corpus checksum mismatch.' }
    [IO.File]::Copy($frequency, (Join-Path $root 'dict\uk-50k.txt'), $true)
    Get-FileHash -LiteralPath (Join-Path $target 'uk_UA.dic'), (Join-Path $target 'uk_UA.aff'), (Join-Path $root 'dict\uk-50k.txt') -Algorithm SHA256
}
finally { $zip.Dispose() }
