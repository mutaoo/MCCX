# 只截 MccX 窗口区域：主界面 / 砍怪下拉 / 鼠标下拉 / 重连下拉
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MccXShot {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
}
"@

$out = "$(Join-Path $PSScriptRoot '..\artifacts')"

function Grab-Win {
    param([string]$Path, [int]$Margin = 60)
    $h = [IntPtr]$script:Hwnd
    $r = New-Object MccXShot+RECT
    [void][MccXShot]::GetWindowRect($h, [ref]$r)
    $x = [Math]::Max(0, $r.L - $Margin)
    $y = [Math]::Max(0, $r.T - $Margin)
    $w = ($r.R - $r.L) + ($Margin * 2)
    $hgt = ($r.B - $r.T) + ($Margin * 2)
    $bmp = New-Object System.Drawing.Bitmap $w, $hgt
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen((New-Object System.Drawing.Point $x, $y), [System.Drawing.Point]::Empty, (New-Object System.Drawing.Size $w, $hgt))
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host ("saved {0}  (win rect L={1} T={2} R={3} B={4})" -f $Path, $r.L, $r.T, $r.R, $r.B)
}

function Open-Flyout {
    param([string]$BtnName)
    $b = Get-UiButton $BtnName
    $r = $b.Current.BoundingRectangle
    Write-Host ("BTN {0}: x={1} y={2} w={3} h={4}" -f $BtnName, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
    try {
        $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
    } catch { Invoke-Element $b }
    Start-Sleep -Milliseconds 900
}

function Close-Flyout {
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 500
}

$null = Start-App
Set-Foreground
Start-Sleep -Milliseconds 800
Grab-Win "$out\w1_main.png"

Open-Flyout '自动砍怪选项'
try { $r = (Get-UiEdit '攻击距离').Current.BoundingRectangle; Write-Host ("POP attack: x={0} y={1} w={2} h={3}" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height) } catch { Write-Host 'POP attack: not found' }
Grab-Win "$out\w2_attack.png" 150
Close-Flyout

Open-Flyout '鼠标控制选项'
try { $r = (Get-UiCombo '鼠标模式').Current.BoundingRectangle; Write-Host ("POP mouse: x={0} y={1} w={2} h={3}" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height) } catch { Write-Host 'POP mouse: not found' }
Grab-Win "$out\w3_mouse.png" 250
Close-Flyout

try {
    Open-Flyout '自动重连选项'
    $r = (Get-UiEdit '重连次数').Current.BoundingRectangle
    Write-Host ("POP recon: x={0} y={1} w={2} h={3}" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height)
    Grab-Win "$out\w4_recon.png" 200
    Close-Flyout
} catch { Write-Host 'recon flyout: FAIL' }

Write-Host "done"
exit 0
