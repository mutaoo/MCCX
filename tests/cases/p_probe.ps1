# 探针：攻击距离输入框的值随时间是否变化（判断脏字符来源）
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ProbeWin {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    public static string FgTitle() {
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(GetForegroundWindow(), sb, 256);
        return sb.ToString();
    }
}
"@

function Open-Flyout {
    param([string]$BtnName)
    $b = Get-UiButton $BtnName
    try {
        $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
    } catch { Invoke-Element $b }
    Start-Sleep -Milliseconds 700
}

$null = Start-App
Save-Foreground
try {
    Open-Flyout '自动砍怪选项'
    for ($i = 0; $i -lt 8; $i++) {
        $v = ''
        try { $v = Get-EditValue (Get-UiEdit '攻击距离') } catch { $v = '<err>' }
        $focus = ''
        try {
            $ae = [System.Windows.Automation.AutomationElement]::FocusedElement
            if ($ae) { $focus = "$($ae.Current.ControlType.ProgrammaticName):$($ae.Current.Name)" }
        } catch { }
        $fg = [ProbeWin]::FgTitle()
        Write-Host ("t={0,3}s  value='{1}'  focused={2}  fg='{3}'" -f ($i * 2), $v, $focus, $fg)
        Start-Sleep -Seconds 2
    }
    Close-AllFlyouts
} finally {
    Restore-Foreground
    Stop-App
}
exit 0
