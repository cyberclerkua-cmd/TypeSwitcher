param(
    [Parameter(Mandatory = $true)][string]$Directory,
    [string]$Reports = '',
    [string]$TemporaryRoot = $env:TEMP
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Directory).Path
foreach ($name in @('TypeSwitcher.exe', 'LICENSE', 'PresentationNative_cor3.dll', 'dict\ru_RU.dic', 'dict\ru_RU.aff', 'dict\en_US.dic', 'dict\en_US.aff', 'dict\uk_UA.dic', 'dict\uk_UA.aff', 'dict\uk_UA.LICENSE', 'dict\uk-50k.txt', 'dict\SOURCES.md', 'assets\typeswitcher.png', 'assets\typeswitcher-action.png', 'assets\spell.wav')) {
    if (-not (Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)) { throw "Missing published file: $name" }
}
$hashes = @{
    'dict\uk_UA.dic' = 'B73062ADA50324D2575121C9734C18789D67EB4502AF6AE12ED62DEF7D0528B4'
    'dict\uk_UA.aff' = '3222E8D5F88FD1DB291A74C59E8FC5541BB2CC3D3AC29C6882DF4F3DBB051A32'
    'dict\uk-50k.txt' = '881686FE66A168B79F6A9EE2F6CBBF944CFB12BFB7040913EF535CB29BD86EC9'
}
foreach ($name in $hashes.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $source $name) -Algorithm SHA256).Hash -ne $hashes[$name]) { throw "Checksum mismatch: $name" }
}
if (-not (Test-Path -LiteralPath $TemporaryRoot)) { throw 'Temporary root directory is missing.' }
$temporary = Join-Path $TemporaryRoot ('TypeSwitcher package test ' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
try {
    # Run the self-contained application from a relocated directory with clean data.
    robocopy $source $temporary /E /XD (Join-Path $source 'data') /XF *.pdb /NFL /NDL /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw 'Could not copy the published package.' }
    foreach ($mode in @('--selftest', '--eval --language all', '--check ghbdsn --language en')) {
        $process = Start-Process -FilePath (Join-Path $temporary 'TypeSwitcher.exe') -ArgumentList $mode -PassThru
        if (-not $process.WaitForExit(120000)) { $process.Kill(); throw "Timed out: $mode" }
        if ($process.ExitCode -ne 0) { throw "Failed: $mode (exit $($process.ExitCode)); see typeswitcher_* reports in TEMP." }
        "Passed: $mode"
    }
    if ($Reports -ne '') {
        [IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($Reports)) | Out-Null
        foreach ($name in @('typeswitcher_selftest.txt', 'typeswitcher_eval.txt', 'typeswitcher_word_probe.txt')) {
            [IO.File]::Copy((Join-Path $env:TEMP $name), (Join-Path $Reports $name), $true)
        }
    }
}
finally { if (Test-Path -LiteralPath $temporary) { [IO.Directory]::Delete($temporary, $true) } }
'Published package verified.'
exit 0
