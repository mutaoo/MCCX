# 精确测量：屏幕高度、参数弹层 popup 的窗口矩形、子列表可见范围
# 注意：生物过滤已改成模态弹窗（不再有嵌套子弹层），弹窗的几何验收看 v_ui7.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class WinEnum {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int n);
}
'@

function Find-NamedAny {
    param([string]$Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)
    return $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

$null = Start-App
Save-Foreground
try {
    Write-Host ("screen = {0} x {1}" -f [WinEnum]::GetSystemMetrics(0), [WinEnum]::GetSystemMetrics(1))

    $lbl = Get-UiButton '自动砍怪选项'
    try {
        $ec = $lbl.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
    } catch { Invoke-Element $lbl }
    Start-Sleep -Milliseconds 900

    $exp = Find-NamedAny '生物过滤'
    try {
        $ec2 = $exp.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec2.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec2.Expand() }
    } catch {
        try { $tp = $exp.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
              if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $tp.Toggle() } } catch {
            $ip = $exp.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $ip.Invoke() }
    }
    Start-Sleep -Milliseconds 1200

    # 关键控件矩形
    foreach ($pair in @(@('过滤模式', 'ComboBox'), @('敌对生物全选', 'CheckBox'), @('僵尸', 'CheckBox'), @('村民', 'CheckBox'))) {
        $el = Find-UiElement $script:CT::$($pair[1]) $pair[0]
        if (-not $el) { $el = Find-NamedAny $pair[0] }
        if ($el) {
            $r = $el.Current.BoundingRectangle
            if ([double]::IsInfinity($r.Y)) { Write-Host ("{0,-10} rect=<∞>" -f $pair[0]) }
            else { Write-Host ("{0,-10} y={1}..{2}  x={3}..{4}" -f $pair[0], [int]$r.Y, [int]($r.Y + $r.Height), [int]$r.X, [int]($r.X + $r.Width)) }
        } else { Write-Host ("{0,-10} NOT FOUND" -f $pair[0]) }
    }

    # 该进程所有可见窗口（找出 flyout popup 的 HWND 和底边）
    $pid2 = (Get-Process MCCX | Sort-Object StartTime -Descending | Select-Object -First 1).Id
    Write-Host "pid=$pid2"
    $sb = New-Object System.Text.StringBuilder 256
    $list = New-Object System.Collections.ArrayList
    $cb = [WinEnum+EnumProc]{
        param($h, $l)
        $p = 0
        [void][WinEnum]::GetWindowThreadProcessId($h, [ref]$p)
        if ($p -eq $pid2 -and [WinEnum]::IsWindowVisible($h)) {
            [void][WinEnum]::GetClassName($h, $sb, 256)
            $r2 = New-Object WinEnum+RECT
            [void][WinEnum]::GetWindowRect($h, [ref]$r2)
            [void]$list.Add(("  hwnd={0} cls={1} rect=({2},{3})-({4},{5})" -f $h, $sb.ToString(), $r2.L, $r2.T, $r2.R, $r2.B))
        }
        return $true
    }
    [void][WinEnum]::EnumWindows($cb, [IntPtr]::Zero)
    $list | ForEach-Object { Write-Host $_ }
    Write-Host 'done'
} finally {
    try { Close-AllFlyouts } catch { }
    Restore-Foreground
    Stop-App
}
exit 0
