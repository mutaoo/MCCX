# 视觉验收：截图新 UI + 验证切账号后日志滚到底 + 悬停叉号 + 二次确认弹窗（不删账号、不连服）
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class VuiCursor {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
}
"@

$out = "$(Join-Path $PSScriptRoot '..\artifacts')"
$fail = 0

function Check {
    param([bool]$Cond, [string]$What, [string]$Detail = '')
    if ($Cond) { Write-Host "PASS  $What" }
    else { Write-Host "FAIL  $What $Detail"; $script:fail++ }
}

function Grab {
    param([string]$Path)
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $Path"
}

$null = Start-App
Set-Foreground
Start-Sleep -Milliseconds 1200

try {
    $list = Find-ById $script:AppRoot 'AccountList'
    if (-not $list) { throw 'AccountList not found' }
    $items = @(Get-ListItems $list)
    Check ($items.Count -gt 0) "账号列表有 $($items.Count) 项"

    Grab "$out\v1_main.png"

    # ---- 1. 切换账号后日志应停在最新一行 ----
    foreach ($idx in @(0, [Math]::Min(1, $items.Count - 1), 0)) {
        Select-Item $items[$idx]
        Start-Sleep -Milliseconds 900
        $log = Find-ById $script:AppRoot 'LogList'
        $pct = $null; $scrollable = $false
        try {
            $sp = $log.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            $scrollable = $sp.Current.VerticallyScrollable
            if ($scrollable) { $pct = $sp.Current.VerticalScrollPercent }
        } catch { }
        if (-not $scrollable) {
            Write-Host "  item[$idx] 日志不足一屏，跳过滚动检查"
        } else {
            Check ($pct -ge 99) "切到第 $idx 个账号后日志滚到底" "pct=$pct"
        }
    }

    # ---- 2. 版本框：可输入 + 读回 ----
    $ver = $null
    try { $ver = Find-ById $script:AppRoot 'VersionBox' } catch { }
    Check ($null -ne $ver) '版本控件存在'
    if ($ver) {
        Check ($ver.Current.ControlType.ProgrammaticName -eq 'ControlType.ComboBox') '版本是下拉框(ComboBox)' $ver.Current.ControlType.ProgrammaticName
        $text = ''
        try {
            $inner = $ver.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Edit)))
            if ($inner) { $text = [string]$inner.Current.Name }
        } catch { }
        Write-Host "  版本当前值 = [$text]"
        Check ($text.Length -gt 0) '版本框有值（auto 或手动输入的版本号）'
    }

    # ---- 3. 悬停账号项 → 右侧叉号淡入 ----
    if ($items.Count -gt 0) {
        $beforeCount = $items.Count
        Select-Item $items[0]
        Start-Sleep -Milliseconds 500
        $xbtn = $items[0].FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Button)),
                (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, '删除账号')))))
        Check ($null -ne $xbtn) '账号项内有删除叉号'
        if ($xbtn) {
            $r = $xbtn.Current.BoundingRectangle
            [void][VuiCursor]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
            Start-Sleep -Milliseconds 600
            Grab "$out\v2_hover_delete.png"
        }
    }

    # ---- 4. 点叉号 → 二次确认弹窗（再取消，不动账号）----
    if ($xbtn) {
        Invoke-Element $xbtn
        $ok = $null
        for ($i = 0; $i -lt 20 -and -not $ok; $i++) {
            Start-Sleep -Milliseconds 250
            $ok = Find-UiElement $script:CT::Button '取消'
        }
        Check ($null -ne $ok) '点叉号弹出二次确认窗口'
        if ($ok) {
            Grab "$out\v3_delete_confirm.png"
            Invoke-Element $ok   # 取消：不删任何账号
            Start-Sleep -Milliseconds 500
            Check (@(Get-ListItems $list).Count -eq $beforeCount) '取消后账号数不变（没误删）' "before=$beforeCount after=$(@(Get-ListItems $list).Count)"
        }
    }

    Write-Host "FAILURES=$fail"
} finally {
    Restore-Foreground
    Stop-App
}
exit $fail
