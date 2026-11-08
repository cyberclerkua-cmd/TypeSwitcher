param([string]$Output = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($Output -eq '') { $Output = Join-Path $root ('artifacts\source-checkpoint-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip') }
$parent = Split-Path -Parent $Output
if (-not (Test-Path -LiteralPath $parent)) { throw "Missing output parent: $parent" }
$excluded = @('.git', 'bin', 'obj', 'dist', 'artifacts', 'TestResults')
function SourceFiles([string]$directory) {
    foreach ($file in [IO.Directory]::EnumerateFiles($directory)) { $file }
    foreach ($child in [IO.Directory]::EnumerateDirectories($directory)) {
        if ($excluded -contains [IO.Path]::GetFileName($child)) { continue }
        if (([IO.File]::GetAttributes($child) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        SourceFiles $child
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$files = @(SourceFiles $root)
$manifest = @()
$stream = [IO.File]::Open($Output, [IO.FileMode]::CreateNew)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $relative = $file.Substring($root.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        $manifest += @{ path = $relative; sha256 = $hash; bytes = ([IO.FileInfo]$file).Length }
    }
    $entry = $zip.CreateEntry('CHECKPOINT-MANIFEST.json')
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write(($manifest | ConvertTo-Json -Depth 4)) } finally { $writer.Dispose() }
}
finally { $zip.Dispose(); $stream.Dispose() }
$check = [IO.Compression.ZipFile]::OpenRead($Output)
try {
    if ($check.Entries.Count -ne $files.Count + 1) { throw 'Checkpoint entry count mismatch.' }
}
finally { $check.Dispose() }
"Checkpoint: $Output"
"Source files: $($files.Count)"
"SHA256: $((Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash)"
foreach ($manifestFile in [IO.Directory]::EnumerateFiles((Join-Path $root 'artifacts'), '*.zip.sha256')) {
    $archive = $manifestFile.Substring(0, $manifestFile.Length - '.sha256'.Length)
    if (-not (Test-Path -LiteralPath $archive)) { throw "Archive is missing: $archive" }
    $expected = ([IO.File]::ReadAllText($manifestFile).Trim() -split '\s+')[0]
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    if ($hash -ne $expected) { throw "Archive checksum mismatch: $archive" }
    "Archive SHA256 verified: $([IO.Path]::GetFileName($archive))"
}
