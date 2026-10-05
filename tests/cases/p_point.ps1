# 探测砍怪组内 空隙 里到底是什么元素
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$out = "$(Join-Path $PSScriptRoot '..\artifacts')\frompoint.txt"
$lines = New-Object System.Collections.Generic.List[string]

$root = Start-App
Set-Foreground
Start-Sleep -Seconds 1

$pts = @(
    @(870, 428),    # ToggleSwitch 起始
    @(1000, 428),   # ToggleSwitch 报告的右边界外
    @(1040, 428),   # 空隙中部
    @(1100, 428),   # 空隙中部
    @(1160, 428),   # 空隙右端
    @(1200, 428),   # 下拉按钮报告区域内
    @(1250, 428)    # 下拉按钮内
)
foreach ($p in $pts) {
    try {
        $el = [System.Windows.Automation.AutomationElement]::FromPoint((New-Object System.Windows.Point $p[0], $p[1]))
        if ($null -eq $el) { $lines.Add(("({0},{1}) -> null" -f $p[0], $p[1])); continue }
        $r = $el.Current.BoundingRectangle
        $ct = $el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        $name = ($el.Current.Name -replace "`r|`n", ' ')
        $lines.Add(("({0},{1}) -> {2} [{3}] aid={4} rect=({5},{6},{7},{8})" -f $p[0], $p[1], $ct, $name, $el.Current.AutomationId, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height))
    } catch {
        $lines.Add(("({0},{1}) -> ERROR {2}" -f $p[0], $p[1], $_.Exception.Message))
    }
}

# 顺便：鼠标实际指针位置（看是否悬停导致 tooltip）
Add-Type -AssemblyName System.Windows.Forms
$mp = [System.Windows.Forms.Cursor]::Position
$lines.Add("cursor = $($mp.X),$($mp.Y)")

# 砍怪组内每个直接子元素按 X 排序的边界
$lines.Add('')
$lines.Add('--- 砍怪组相关元素 ---')
foreach ($t in @('自动砍怪', '自动砍怪选项')) {
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $t)
    foreach ($f in ($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond))) {
        $r = $f.Current.BoundingRectangle
        $lines.Add(("{0,-12} {1} rect=({2},{3},{4},{5})" -f $t, $f.Current.ControlType.ProgrammaticName, [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height))
    }
}

[IO.File]::WriteAllText($out, ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding($true)))
Get-Content $out -Encoding UTF8
exit 0
