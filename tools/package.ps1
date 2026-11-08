param(
    [string]$Directory = '',
    [string]$Output = '',
    [string]$TemporaryRoot = $env:TEMP,
    [string]$Readme = '',
    [switch]$OmitDocs
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($Directory -eq '') { $Directory = Join-Path $root 'dist\TypeSwitcher' }
if ($Output -eq '') { $Output = Join-Path $root 'artifacts' }
if (-not (Test-Path -LiteralPath (Join-Path $Directory 'TypeSwitcher.exe'))) { throw 'Publish the application first.' }
$Directory = (Resolve-Path -LiteralPath $Directory).Path
if ($Readme -ne '' -and -not (Test-Path -LiteralPath $Readme -PathType Leaf)) { throw 'Release README is missing.' }
$project = [xml][IO.File]::ReadAllText((Join-Path $root 'src\TypeSwitcher\TypeSwitcher.csproj'))
$version = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not (Test-Path -LiteralPath $TemporaryRoot)) { throw 'Temporary root directory is missing.' }
$temporary = Join-Path $TemporaryRoot ('typeswitcher-archive-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    $excludedDirectories = @((Join-Path $Directory 'data'))
    if ($OmitDocs) { $excludedDirectories += Join-Path $Directory 'docs' }
    robocopy $Directory $temporary /E /XD $excludedDirectories /XF *.pdb CHANGELOG* /NFL /NDL /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw 'Could not stage application files.' }
    if ($Readme -ne '') { [IO.File]::Copy([IO.Path]::GetFullPath($Readme), (Join-Path $temporary 'README.md'), $true) }
    [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Output)) | Out-Null
    $archive = Join-Path $Output "TypeSwitcher-$version-win-x64.zip"
    Compress-Archive -Path (Join-Path $temporary '*') -DestinationPath $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
    [IO.File]::WriteAllText($archive + '.sha256', "$hash  $([IO.Path]::GetFileName($archive))`n")
    "Created: $archive"
    "SHA256: $hash"
}
finally { if (Test-Path -LiteralPath $temporary) { [IO.Directory]::Delete($temporary, $true) } }
exit 0
