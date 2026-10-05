# PrintWindow 抓取被遮挡的 MccX 窗口内容
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class MccXPrint {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
}
"@

$out = "$(Join-Path $PSScriptRoot '..\artifacts')"
$null = Start-App
$h = [IntPtr]$script:Hwnd

$r = New-Object MccXPrint+RECT
[void][MccXPrint]::GetWindowRect($h, [ref]$r)
$w = $r.R - $r.L
$ht = $r.B - $r.T
Write-Host "window $w x $ht"

$bmp = New-Object System.Drawing.Bitmap $w, $ht
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
$ok = [MccXPrint]::PrintWindow($h, $hdc, 3)   # PW_RENDERFULLCONTENT | PW_CLIENTONLY
$g.ReleaseHdc($hdc)
$g.Dispose()

# 检查是否全黑
$black = $true
for ($i = 0; $i -lt 50 -and $black; $i++) {
    $c = $bmp.GetPixel(($w / 2), [int]($ht * $i / 50))
    if ($c.R -gt 30 -or $c.G -gt 30 -or $c.B -gt 30) { $black = $false }
}
$bmp.Save("$out\pw_main.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "PrintWindow ok=$ok  allBlack=$black  -> $out\pw_main.png"
exit 0
