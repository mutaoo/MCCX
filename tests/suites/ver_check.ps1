$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class V {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int s);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool f);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
}
"@
$exe = "$((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) + '\MCCX.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64')\MCCX.exe"
$p = Start-Process $exe -WorkingDirectory (Split-Path $exe) -PassThru
for ($i = 0; $i -lt 60 -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
if ($p.MainWindowHandle -eq 0) { throw 'no window' }
Start-Sleep -Seconds 3
$p.Refresh()
'native title = [' + $p.MainWindowTitle + ']'
$r = New-Object V+RECT
[void][V]::DwmGetWindowAttribute($p.MainWindowHandle, 9, [ref]$r, 16)
$w = $r.R - $r.L; $h = $r.B - $r.T
'dwm frame = ' + $w + 'x' + $h
# PrintWindow reads the window's own rendering: works even when occluded, never steals focus
$cropH = [Math]::Min(240, $h)
$bmp = New-Object System.Drawing.Bitmap $w, $cropH
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [V]::PrintWindow($p.MainWindowHandle, $hdc, 2)
$g.ReleaseHdc($hdc)
$g.Dispose()
'printwindow ok = ' + $ok
# blank detection: sample luminance at title area / content area
$px1 = $bmp.GetPixel(40, 15); $px2 = $bmp.GetPixel([int]($w/2), 100)
'lum title(40,15) = ' + [int](($px1.R*299 + $px1.G*587 + $px1.B*114)/1000) + '  lum content = ' + [int](($px2.R*299 + $px2.G*587 + $px2.B*114)/1000)
$out = "$(Join-Path $PSScriptRoot '..\artifacts')\_ver_check.png"
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
'saved ' + $out
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$idCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'AppTitleBar')
$tb = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $idCond)
if ($tb) {
    $ctCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $texts = @()
    foreach ($t in $tb.FindAll([System.Windows.Automation.TreeScope]::Descendants, $ctCond)) { $texts += $t.Current.Name }
    'titlebar texts: [' + ($texts -join ' | ') + ']'
} else { 'AppTitleBar not found' }
$p.CloseMainWindow() | Out-Null
Start-Sleep -Seconds 3
$p.Refresh()
if (-not $p.HasExited) { try { $p.Kill() } catch { } }
'procs left = ' + @(Get-Process -Name MCCX -ErrorAction SilentlyContinue).Count
