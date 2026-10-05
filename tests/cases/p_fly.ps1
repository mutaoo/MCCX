# 打开参数下拉，用 PrintWindow 截窗口（不受其他窗口遮挡影响）
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MccXPw {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@

$out = "$(Join-Path $PSScriptRoot '..\artifacts')"

function Grab {
    param([string]$Path)
    $h = [IntPtr]$script:Hwnd
    $r = New-Object MccXPw+RECT
    [void][MccXPw]::GetWindowRect($h, [ref]$r)
    $w = $r.R - $r.L
    $ht = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [void][MccXPw]::PrintWindow($h, $hdc, 3)
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "saved $Path"
}

$null = Start-App
Start-Sleep -Seconds 2

foreach ($t in @(@('自动砍怪选项', 'f_attack'), @('鼠标控制选项', 'f_mouse'), @('自动重连选项', 'f_recon'))) {
    $b = $null
    try { $b = Get-UiButton $t[0] } catch { Write-Host "BTN $($t[0]) not found"; continue }
    $r = $b.Current.BoundingRectangle
    try {
        $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
    } catch { Invoke-Element $b }
    Start-Sleep -Milliseconds 1000
    # 弹层是否可见：从桌面树找 PopupWindowSiteBridge
    $pid2 = (Get-Process MCCX | Select-Object -First 1).Id
    $paneCond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ClassNameProperty, 'Microsoft.UI.Content.PopupWindowSiteBridge')),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, $pid2)))
    $pane = $script:AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $paneCond)
    if ($pane) {
        $pr = $pane.Current.BoundingRectangle
        Write-Host ("{0}: btn=({1},{2}) popup=({3},{4},{5},{6})" -f $t[0], [int]$r.X, [int]$r.Y, [int]$pr.X, [int]$pr.Y, [int]$pr.Width, [int]$pr.Height)
    } else { Write-Host "$($t[0]): popup NOT FOUND" }
    Grab "$out\$($t[1]).png"
    try {
        $ec2 = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $ec2.Collapse()
    } catch { }
    Start-Sleep -Milliseconds 600
}
Write-Host "done"
exit 0
