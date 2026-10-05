# 单独验证“鼠标控制”下拉：打开后列出本进程在桌面树上的可见顶层元素
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
$out = "$(Join-Path $PSScriptRoot '..\artifacts')\mousefly.txt"
$lines = New-Object System.Collections.Generic.List[string]

function To-Int([double]$v) {
    if ([double]::IsNaN($v) -or [double]::IsInfinity($v)) { return -9999 }
    return [int][Math]::Round($v)
}

$root = Start-App
$procId = (Get-Process MCCX | Select-Object -First 1).Id

$b = Get-UiButton '鼠标控制选项'
$br = $b.Current.BoundingRectangle
$lines.Add(("btn rect=({0},{1},{2},{3})" -f [int]$br.X, [int]$br.Y, [int]$br.Width, [int]$br.Height))
try {
    $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $lines.Add("state before = $($ec.Current.ExpandCollapseState)")
    $ec.Expand()
    Start-Sleep -Milliseconds 1200
    $lines.Add("state after  = $($ec.Current.ExpandCollapseState)")
} catch {
    $lines.Add("expand error: $($_.Exception.Message)")
}

# 桌面树里本进程的顶层元素
$pidCond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, $procId)
$els = $script:AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $pidCond)
$lines.Add("top-level elements of pid=$procId : $($els.Count)")
foreach ($e in $els) {
    $r = $e.Current.BoundingRectangle
    $ct = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
    $nm = ($e.Current.Name) -replace "`r|`n", ' '
    $lines.Add("  $ct class=$($e.Current.ClassName) name=$nm off=$($e.Current.IsOffscreen) rect=($(To-Int $r.X),$(To-Int $r.Y),$(To-Int $r.Width),$(To-Int $r.Height))")
}

# 再按名称找“鼠标模式”
try {
    $cb = Get-UiCombo '鼠标模式'
    $r = $cb.Current.BoundingRectangle
    $lines.Add(("combo found rect=({0},{1},{2},{3})" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height))
} catch { $lines.Add("combo NOT FOUND: $($_.Exception.Message)") }

try { $ec2 = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $ec2.Collapse() } catch { }

[IO.File]::WriteAllText($out, ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding($true)))
Get-Content $out -Encoding UTF8
exit 0
