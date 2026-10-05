# 把 MccX 调到前台并截取它自己的窗口（解决被 Minecraft 遮挡）
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MccXShot2 {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SwitchToThisWindow(IntPtr h, bool fUnknown);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
}
"@

$out = "$(Join-Path $PSScriptRoot '..\artifacts')"

$root = Start-App
$h = [IntPtr]$script:Hwnd

[void][MccXShot2]::ShowWindow($h, 9)
[void][MccXShot2]::SwitchToThisWindow($h, $true)
Start-Sleep -Milliseconds 800
[void][MccXShot2]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 800
Write-Host ("foreground is MccX: {0}" -f ([MccXShot2]::GetForegroundWindow() -eq $h))

function Grab {
    param([string]$Path, [int]$Margin = 0)
    $r = New-Object MccXShot2+RECT
    [void][MccXShot2]::GetWindowRect($h, [ref]$r)
    $x = [Math]::Max(0, $r.L + $Margin)
    $y = [Math]::Max(0, $r.T + $Margin)
    $w = ($r.R - $r.L) - ($Margin * 2)
    $ht = ($r.B - $r.T) - ($Margin * 2)
    $bmp = New-Object System.Drawing.Bitmap $w, $ht
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen((New-Object System.Drawing.Point $x, $y), [System.Drawing.Point]::Empty, (New-Object System.Drawing.Size $w, $ht))
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $Path ($w x $ht)"
}

Grab "$out\z1_main.png"

# 读取连接参数框真实内容
foreach ($n in @('ServerBox', 'PortBox', 'UserBox', 'VersionBox')) {
    $el = Find-ById $root $n
    if ($el) {
        try {
            $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            Write-Host ("{0} value = [{1}]" -f $n, $vp.Current.Value)
        } catch { Write-Host "$n : no value pattern" }
    } else { Write-Host "$n : not found" }
}

# 打开砍怪下拉后截图
try {
    $b = Get-UiButton '自动砍怪选项'
    try {
        $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
    } catch { Invoke-Element $b }
    Start-Sleep -Milliseconds 1200
    Grab "$out\z2_flyout.png"
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 500
} catch { Write-Host "flyout open failed: $($_.Exception.Message)" }

Write-Host "done"
exit 0
