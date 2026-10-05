# 嵌套下拉验收：参数弹层 -> 点“生物过滤”再开一层；父层是否还在、列表是否滚得全、弹层有没有出屏幕
# 注意：本脚本测的“嵌套子下拉”已不存在——生物过滤现是模态弹窗，验收改用 v_ui7.ps1（本脚本已作废）
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TopHelper2 {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool fUnknown);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_SHOWWINDOW = 0x0040;
}
'@
function Raise-Mccx {
    $h = [IntPtr]$script:Hwnd
    $null = [TopHelper2]::ShowWindow($h, 9)
    [void][TopHelper2]::SetWindowPos($h, [TopHelper2]::HWND_TOPMOST, 0, 0, 0, 0,
        [TopHelper2]::SWP_NOMOVE -bor [TopHelper2]::SWP_NOSIZE -bor [TopHelper2]::SWP_SHOWWINDOW)
    [TopHelper2]::SwitchToThisWindow($h, $true)
    Start-Sleep -Milliseconds 700
    [void][TopHelper2]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 900
    $fg = [TopHelper2]::GetForegroundWindow()
    Write-Host ("  [fg] target={0} now={1} match={2}" -f $h, $fg, ($fg -eq $h))
}
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class WinEnum2 {
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

$out = "$(Join-Path $PSScriptRoot '..\artifacts')"

function Find-NamedAny {
    param([string]$Name)
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)
    $el = $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if (-not $el) {
        $desktop = [System.Windows.Automation.AutomationElement]::RootElement
        $c2 = New-Object System.Windows.Automation.AndCondition(
            $cond,
            (New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, (Get-MainProcId))))
        $el = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c2)
    }
    return $el
}

function Open-ByAnyPattern {
    param($El)
    try {
        $ec = $El.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand(); return $true }
        return $true
    } catch { }
    try {
        $tp = $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $tp.Toggle() }
        return $true
    } catch { }
    try { $ip = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern); $ip.Invoke(); return $true } catch { }
    return $false
}

function Test-Has {
    param([string]$Name, [string]$CT)
    $el = $null
    if ($CT -eq 'Edit') { $el = Find-UiElement $script:CT::Edit $Name }
    elseif ($CT -eq 'ComboBox') { $el = Find-UiElement $script:CT::ComboBox $Name }
    elseif ($CT -eq 'CheckBox') { $el = Find-UiElement $script:CT::CheckBox $Name }
    if (-not $el) { $el = Find-NamedAny $Name }
    if (-not $el) { return '缺' }
    $r = $el.Current.BoundingRectangle
    if ([double]::IsInfinity($r.Y)) { return '在树但未测量' }
    return ('ok y={0}..{1} x={2}..{3}' -f [int]$r.Y, [int]($r.Y + $r.Height), [int]$r.X, [int]($r.X + $r.Width))
}

$null = Start-App
Save-Foreground
try {
    Write-Host "script:Hwnd=$script:Hwnd  mainpid=$(Get-MainProcId)"
    Raise-Mccx
    $sw = [WinEnum2]::GetSystemMetrics(0); $sh = [WinEnum2]::GetSystemMetrics(1)
    Write-Host "screen = $sw x $sh"
    $mainHwnd = [IntPtr]$script:Hwnd

    $lbl = Get-UiButton '自动砍怪选项'
    if (-not (Open-ByAnyPattern $lbl)) { throw '开参数弹层失败' }
    Start-Sleep -Milliseconds 900
    Write-Host ("1) 参数弹层: 距离 = " + (Test-Has '攻击距离' 'Edit'))

    $exp = Find-NamedAny '生物过滤'
    if (-not $exp) { throw '找不到“生物过滤”子列表' }
    Write-Host ("   生物过滤 控件=" + $exp.Current.ControlType.ProgrammaticName)
    [void](Open-ByAnyPattern $exp)
    Start-Sleep -Milliseconds 1200

    Write-Host '2) 两层都在？'
    Write-Host ("   距离(父层) = " + (Test-Has '攻击距离' 'Edit'))
    Write-Host ("   过滤模式   = " + (Test-Has '过滤模式' 'ComboBox'))
    Write-Host ("   敌对全选   = " + (Test-Has '敌对生物全选' 'CheckBox'))
    Write-Host ("   僵尸       = " + (Test-Has '僵尸' 'CheckBox'))
    Write-Host ("   中立全选   = " + (Test-Has '中立生物全选' 'CheckBox'))

    # 滚到底再看最后一项
    $sc = Find-NamedAny '生物过滤'
    $sv = $null
    try {
        $svCond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::ScrollBar)
        $sbs = $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $svCond)
        Write-Host ("   滚动条个数=" + $sbs.Count)
    } catch { }

    # 各弹层窗口
    Write-Host '3) 可见窗口：'
    $pid2 = (Get-Process MCCX | Sort-Object StartTime -Descending | Select-Object -First 1).Id
    $sb = New-Object System.Text.StringBuilder 256
    $cb = [WinEnum2+EnumProc]{
        param($h, $l)
        $p = 0
        [void][WinEnum2]::GetWindowThreadProcessId($h, [ref]$p)
        if ($p -eq $pid2 -and [WinEnum2]::IsWindowVisible($h)) {
            [void][WinEnum2]::GetClassName($h, $sb, 256)
            $r2 = New-Object WinEnum2+RECT
            [void][WinEnum2]::GetWindowRect($h, [ref]$r2)
            $tag = if ($h -eq $script:Hwnd) { ' [主窗]' } else { '' }
            $off = if ($r2.B -gt 1790 -or $r2.R -gt 2870) { '  <<< 出屏幕' } else { '' }
            Write-Host ("   {0} {1} ({2},{3})-({4},{5}){6}{7}" -f $h, $sb.ToString(), $r2.L, $r2.T, $r2.R, $r2.B, $tag, $off)
        }
        return $true
    }
    [void][WinEnum2]::EnumWindows($cb, [IntPtr]::Zero)

    Raise-Mccx
    $b = New-Object System.Drawing.Bitmap $sw, $sh
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size($sw, $sh)))
    $b.Save("$out\v5b_nested.png", [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $b.Dispose()
    Write-Host 'saved v5b_nested.png'

    # 收起子层，看父层是否还在
    try {
        $ec3 = $exp.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec3.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec3.Collapse() }
    } catch {
        try {
            $tp3 = $exp.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($tp3.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) { $tp3.Toggle() }
        } catch { }
    }
    Start-Sleep -Milliseconds 900
    Write-Host ("4) 收起子层后: 距离(父层) = " + (Test-Has '攻击距离' 'Edit') + " / 过滤模式 = " + (Test-Has '过滤模式' 'ComboBox'))
    Write-Host 'done'
} finally {
    try { Close-AllFlyouts } catch { }
    [void][TopHelper2]::SetWindowPos([IntPtr]$script:Hwnd, [TopHelper2]::HWND_NOTOPMOST, 0, 0, 0, 0,
        [TopHelper2]::SWP_NOMOVE -bor [TopHelper2]::SWP_NOSIZE -bor [TopHelper2]::SWP_SHOWWINDOW)
    Restore-Foreground
    Stop-App
}
exit 0
