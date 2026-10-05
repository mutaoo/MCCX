# 诊断：鼠标参数下拉展开后，为什么有时找不到 [按住毫秒]
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$out = "$(Join-Path $PSScriptRoot '..\artifacts')\mouse_dump.txt"
$lines = New-Object System.Collections.Generic.List[string]

function Dump-El($el, [int]$depth, [int]$max) {
    if ($depth -gt $max) { return }
    foreach ($c in $el) {
        try {
            $r = $c.Current.BoundingRectangle
            $lines.Add(("{0}{1} name='{2}' aid={3} x={4} y={5} w={6} h={7}" -f `
                ('  ' * $depth), $c.Current.ControlType.ProgrammaticName -replace 'ControlType\.', '',
                ($c.Current.Name -replace "`r|`n", ' '), $c.Current.AutomationId,
                [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height))
        } catch { $lines.Add(('  ' * $depth) + '(read fail)') }
        Dump-El ($c.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) ($depth + 1) $max
    }
}

$null = Start-App
Set-Foreground
Start-Sleep -Milliseconds 1200

$pidMain = Get-MainProcId
$lines.Add("main pid = $pidMain")

foreach ($round in 1..3) {
    try { Close-AllFlyouts } catch { }
    Start-Sleep -Milliseconds 500

    $b = Get-UiButton '鼠标控制选项'
    $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $state0 = $ec.Current.ExpandCollapseState
    if ($state0 -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
    Start-Sleep -Milliseconds 1800
    $state1 = $ec.Current.ExpandCollapseState

    $el = $null
    try { $el = Find-UiElement $script:CT::Edit '按住毫秒' } catch { $el = $null }
    $lines.Add("round ${round}: stateBefore=$state0 stateAfter=$state1 holdMs=$(if ($el) { 'FOUND' } else { 'MISSING' })")

    if (-not $el) {
        $lines.Add('--- 桌面树里本进程所有 Edit ---')
        $cond = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Edit)),
            (New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, $pidMain)))
        $edits = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants, $cond)
        foreach ($e in $edits) {
            $r = $e.Current.BoundingRectangle
            $lines.Add("  edit name='$($e.Current.Name)' x=$([int]$r.X) y=$([int]$r.Y) w=$([int]$r.Width) h=$([int]$r.Height)")
        }
        $lines.Add('--- PopupWindowSiteBridge 列表 ---')
        $pcond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ClassNameProperty, 'Microsoft.UI.Content.PopupWindowSiteBridge')
        $panes = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants, $pcond)
        foreach ($p in $panes) { $lines.Add("  pane name='$($p.Current.Name)' id=$($p.Current.AutomationId)") }
        $lines.Add('--- 鼠标控制按钮子树 ---')
        Dump-El @($b) 0 6
    }

    $ec.Collapse()
    Start-Sleep -Milliseconds 500
}

$lines | Out-File -FilePath $out -Encoding utf8
Write-Host "dump -> $out"
try { Stop-App } catch { }
exit 0
