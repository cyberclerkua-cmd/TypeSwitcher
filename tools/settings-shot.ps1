# Screenshot of the settings window without touching the user's desktop:
# the window is opened OFF-SCREEN and NOT activated (TypeSwitcher.exe --settings <page> --offscreen),
# rendered via PrintWindow (works for hidden/off-screen windows), then the process is killed.
# No mouse/keyboard simulation, no focus stealing - safe to run while the user is working.
#
# ASCII-only on purpose: Windows PowerShell 5.1 mis-reads BOM-less Cyrillic in .ps1.
#
# Usage:
#   .\tools\settings-shot.ps1                       # page 0 -> settings-0.png in %TEMP%
#   .\tools\settings-shot.ps1 -Page 3 -Out dict.png # page index = order in the left nav
#   .\tools\settings-shot.ps1 -Exe dist\TypeSwitcher\TypeSwitcher.exe
param(
    [int]$Page = 0,
    [string]$Out = "",
    [string]$Exe = "",
    [switch]$Library
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ($Exe -eq "") {
    foreach ($candidate in @('dist\TypeSwitcher\TypeSwitcher.exe', 'src\TypeSwitcher\bin\x64\Debug\net10.0-windows\TypeSwitcher.exe', 'src\TypeSwitcher\bin\Debug\net10.0-windows\TypeSwitcher.exe')) {
        $path = Join-Path $root $candidate
        if (Test-Path -LiteralPath $path) { $Exe = $path; break }
    }
}
if ($Out -eq "") { $Out = Join-Path $env:TEMP "settings-$Page.png" }
if (-not (Test-Path $Exe)) { throw "Not found: $Exe (build first: dotnet build src\TypeSwitcher\TypeSwitcher.csproj)" }

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ShotNative {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public static IntPtr FindByPid(int pid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { found = h; return false; } return true; }, IntPtr.Zero);
    return found;
  }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
# Physical pixels everywhere (per-monitor DPI aware), otherwise rects are virtualized.
[ShotNative]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

$arguments = if ($Library) { '--dictionary-settings --offscreen' } else { "--settings $Page --offscreen" }
$p = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $h = [ShotNative]::FindByPid($p.Id) }
if ($h -eq [IntPtr]::Zero) { $p.Kill(); throw "settings window did not appear" }
Start-Sleep -Milliseconds 900   # let WPF finish the first layout/render pass

$r = New-Object ShotNative+RECT
[ShotNative]::GetWindowRect($h, [ref]$r) | Out-Null
$w = $r.R - $r.L; $hh = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap $w, $hh
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [ShotNative]::PrintWindow($h, $hdc, 2)   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
$p.Kill()
if (-not $ok) { throw "PrintWindow failed" }
"saved $Out ($w x $hh px)"
