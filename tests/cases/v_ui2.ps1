# 复现验收：① 启动时不该有残留删除弹窗 ② 叉号必须与所在账号行一一对应 ③ 悬停淡入 ④ 取消不删账号
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Vui2Cursor {
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

function Get-DeleteDialogText {
    foreach ($t in $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Text)))) {
        $n = [string]$t.Current.Name
        if ($n.Contains('确定删除账号')) { return $n }
    }
    return $null
}

function Close-DeleteDialog {
    $c = Find-UiElement $script:CT::Button '取消'
    if ($c) { Invoke-Element $c; Start-Sleep -Milliseconds 500 }
}

$null = Start-App
Set-Foreground
Start-Sleep -Milliseconds 1500

try {
    $list = Find-ById $script:AppRoot 'AccountList'
    if (-not $list) { throw 'AccountList not found' }
    $items = @(Get-ListItems $list)
    Check ($items.Count -gt 0) "账号列表 $($items.Count) 项"

    # ---- 0) 启动时不该有残留的删除弹窗 ----
    $stale = Find-UiElement $script:CT::Button '删除'
    Check ($null -eq $stale) '启动时没有残留删除弹窗'
    if ($stale) { Close-DeleteDialog }
    Grab "$out\v4_start.png"

    # ---- 1) 逐行叉号：弹窗必须对应点的那一行 ----
    foreach ($name in @('Player', 'TestBob')) {
        $item = Find-ListItemByText $list $name
        if (-not $item) { Check $false "账号 $name 在列表里"; continue }

        Select-Item $item
        Start-Sleep -Milliseconds 500
        $btn = $item.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Button)),
                (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, '删除账号')))))
        Check ($null -ne $btn) "[$name] 行内能找到叉号"
        if (-not $btn) { continue }

        # 悬停 → 叉号淡入（截图人工核对可见性）
        $r = $btn.Current.BoundingRectangle
        [void][Vui2Cursor]::SetCursorPos([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
        Start-Sleep -Milliseconds 700
        Grab "$out\v5_hover_$name.png"

        Invoke-Element $btn
        $dlg = $null
        for ($i = 0; $i -lt 20 -and -not $dlg; $i++) {
            Start-Sleep -Milliseconds 250
            $dlg = Get-DeleteDialogText
        }
        Check ($null -ne $dlg) "[$name] 点叉号弹出二次确认"
        if ($dlg) {
            Check ($dlg.Contains($name)) "[$name] 弹窗对应的是点的那一行" "dlg=$dlg"
            Grab "$out\v6_dialog_$name.png"
            Close-DeleteDialog
            Start-Sleep -Milliseconds 400
            Check (@(Get-ListItems $list).Count -eq $items.Count) "[$name] 取消后账号数不变" `
                "before=$($items.Count) after=$(@(Get-ListItems $list).Count)"
        }
    }

    # 鼠标挪开，避免残留悬停状态
    [void][Vui2Cursor]::SetCursorPos(0, 0)
    Write-Host "FAILURES=$fail"
} finally {
    Restore-Foreground
    Stop-App
}
exit $fail
