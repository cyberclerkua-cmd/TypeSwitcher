param([Parameter(Mandatory = $true)][string]$ForbiddenName)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$excluded = @('.git', 'bin', 'obj', 'dist', 'artifacts', 'TestResults')
$extensions = @('.cs', '.xaml', '.csproj', '.sln', '.json', '.ps1', '.bat', '.yml', '.md', '.html', '.svg', '.manifest')
function Files([string]$directory) {
    foreach ($file in [IO.Directory]::EnumerateFiles($directory)) {
        if ($extensions -contains [IO.Path]::GetExtension($file)) { $file }
    }
    foreach ($child in [IO.Directory]::EnumerateDirectories($directory)) {
        if ($excluded -notcontains [IO.Path]::GetFileName($child)) { Files $child }
    }
}
$issues = @()
foreach ($file in Files $root) {
    $relative = $file.Substring($root.Length + 1)
    if ($relative.IndexOf($ForbiddenName, [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        [IO.File]::ReadAllText($file).IndexOf($ForbiddenName, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $issues += $relative }
}
if ($issues.Count -gt 0) { $issues; throw 'Previous product identity remains in active source files.' }
'Branding audit passed: active source files and names are clean.'
