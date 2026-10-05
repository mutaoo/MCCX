# 验证三个参数下拉：能否打开、弹层内容相对按钮的位置
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"
$out = "$(Join-Path $PSScriptRoot '..\artifacts')\flypos.txt"
$lines = New-Object System.Collections.Generic.List[string]

$root = Start-App

$cases = @(
    @('自动砍怪选项', '攻击距离'),
    @('鼠标控制选项', '左键模式'),
    @('自动重连选项', '重连次数')
)
foreach ($c in $cases) {
    try {
        $b = Get-UiButton $c[0]
        $br = $b.Current.BoundingRectangle
        try {
            $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
        } catch { Invoke-Element $b }
        Start-Sleep -Milliseconds 900

        $content = $null
        try { $content = Get-UiEdit $c[1] } catch { }
        if ($null -eq $content) { try { $content = Get-UiCombo $c[1] } catch { } }
        if ($null -eq $content) { try { $content = Find-UiElement -ControlType Edit -Name $c[1] } catch { } }

        if ($null -eq $content) {
            $lines.Add("$($c[0]): OPEN=NO (找不到内容 $($c[1]))")
        } else {
            $cr = $content.Current.BoundingRectangle
            $lines.Add(("{0}: OPEN=YES btn=({1},{2},{3},{4}) content=({5},{6},{7},{8}) offset=({9},{10})" -f `
                $c[0], [int]$br.X, [int]$br.Y, [int]$br.Width, [int]$br.Height, `
                [int]$cr.X, [int]$cr.Y, [int]$cr.Width, [int]$cr.Height, `
                [int]($cr.X - $br.X), [int]($cr.Y - $br.Y)))
        }

        try {
            $ec2 = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            $ec2.Collapse()
        } catch { }
        Start-Sleep -Milliseconds 500
    } catch {
        $lines.Add("$($c[0]): ERROR $($_.Exception.Message)")
    }
}

[IO.File]::WriteAllText($out, ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding($true)))
Get-Content $out -Encoding UTF8
exit 0
