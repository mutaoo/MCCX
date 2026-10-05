# 布局体检：导出连接参数区 + 自动化区所有控件的屏幕坐标（UTF8 输出，避免控制台乱码）
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$out = "$(Join-Path $PSScriptRoot '..\artifacts')\layout_dump.txt"
$lines = New-Object System.Collections.Generic.List[string]

function To-Int([double]$v) {
    if ([double]::IsNaN($v) -or [double]::IsInfinity($v)) { return -9999 }
    return [int][Math]::Round($v)
}

function Rect($r) {
    return ("x={0} y={1} w={2} h={3}" -f (To-Int $r.X), (To-Int $r.Y), (To-Int $r.Width), (To-Int $r.Height))
}

function Dump-Tree {
    param($El, [int]$Depth, [int]$MaxDepth)
    if ($Depth -gt $MaxDepth) { return }
    foreach ($c in $El) {
        try {
            $r = $c.Current.BoundingRectangle
            $name = ($c.Current.Name -replace "`r|`n", ' ')
            if ($name.Length -gt 36) { $name = $name.Substring(0, 36) + '...' }
            $ct = $c.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            $aid = $c.Current.AutomationId
            $lines.Add(("{0}{1,-11} [{2}] aid={3} {4}" -f (('  ' * $Depth) + '- '), $ct, $name, $aid, (Rect $r)))
        } catch {
            $lines.Add(("{0}(读取失败) {1}" -f ('  ' * $Depth), $_.Exception.Message))
        }
        Dump-Tree ($c.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) ($Depth + 1) $MaxDepth
    }
}

$root = Start-App
Set-Foreground
Start-Sleep -Seconds 1

$lines.Add('===== 关键控件（按名称）=====')
$targets = @('服务器：', '端口：', '用户名：', '版本：', '版本', '连接', '断开', '未连接',
             '自动砍怪', '鼠标控制', '自动钓鱼', '自动重连', '自动补充', '自动行走',
             # 2026-10-05：过滤改成"只显示/只屏蔽"两个独立前缀框；"复制日志"按钮已按用户要求移除
             '服务器信息过滤', '服务器信息只显示前缀', '服务器信息只屏蔽前缀', '服务器信息过滤选项',
             '按服务器分组', '暗色模式切换',
             '自动砍怪选项', '鼠标控制选项', '自动重连选项', '自动钓鱼选项', '视角调整选项', '距离：', '冷却下限：', '冷却上限：')
foreach ($t in $targets) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $t)
    $found = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    if ($found.Count -eq 0) { $lines.Add("MISSING  $t"); continue }
    foreach ($f in $found) {
        $r = $f.Current.BoundingRectangle
        $ct = $f.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        $lines.Add(("{0,-14} {1,-10} {2} offscreen={3}" -f $t, $ct, (Rect $r), $f.Current.IsOffscreen))
    }
}

$lines.Add('')
$lines.Add('===== AutomationPanel 子树 =====')
$apCond = New-Object System.Windows.Automation.PropertyCondition($script:AE::AutomationIdProperty, 'AutomationPanel')
$ap = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $apCond)
if ($ap) {
    $lines.Add(("AutomationPanel  {0}" -f (Rect $ap.Current.BoundingRectangle)))
    Dump-Tree ($ap.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) 1 5
} else { $lines.Add('AutomationPanel NOT FOUND') }

$lines.Add('')
$lines.Add('===== 日志区 =====')
$ll = Find-ById $root 'LogList'
if ($ll) { $lines.Add(("LogList {0}" -f (Rect $ll.Current.BoundingRectangle))) } else { $lines.Add('LogList MISSING') }

$lines.Add('')
$lines.Add('===== 打开“自动砍怪选项”弹层 =====')
try {
    $b = Get-UiButton '自动砍怪选项'
    $lines.Add(("按钮   {0}" -f (Rect $b.Current.BoundingRectangle)))
    try {
        $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
    } catch { Invoke-Element $b }
    Start-Sleep -Milliseconds 1200
    $desktop = $script:AE::RootElement
    $paneCond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ClassNameProperty, 'Microsoft.UI.Content.PopupWindowSiteBridge')),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, (Get-Process MCCX | Select-Object -First 1).Id)))
    $pane = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $paneCond)
    if ($pane) {
        $lines.Add(("弹层 Pane {0}" -f (Rect $pane.Current.BoundingRectangle)))
        Dump-Tree ($pane.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) 1 5
    } else { $lines.Add('弹层 Pane NOT FOUND') }
} catch { $lines.Add("打开失败: $($_.Exception.Message)") }

[IO.File]::WriteAllText($out, ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding($true)))
Write-Host "dumped -> $out  lines=$($lines.Count)"
exit 0
