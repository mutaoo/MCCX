# 全量子树转储（跳过 LogList），定位“关/开”与按钮之间的空隙、状态文字、行溢出
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$out = "$(Join-Path $PSScriptRoot '..\artifacts')\layout_dump2.txt"
$lines = New-Object System.Collections.Generic.List[string]
$script:count = 0

function To-Int([double]$v) {
    if ([double]::IsNaN($v) -or [double]::IsInfinity($v)) { return -9999 }
    return [int][Math]::Round($v)
}

function Dump2 {
    param($El, [int]$Depth)
    if ($script:count -gt 400) { return }
    foreach ($c in $El) {
        try {
            $aid = $c.Current.AutomationId
            if ($aid -eq 'LogList') { $lines.Add(('  ' * $Depth) + '- [LogList ... 跳过]'); continue }
            $r = $c.Current.BoundingRectangle
            $name = ($c.Current.Name -replace "`r|`n", ' ')
            if ($name.Length -gt 44) { $name = $name.Substring(0, 44) + '...' }
            $ct = $c.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
            $lines.Add(("{0}{1,-11} [{2}] aid={3} x={4} y={5} w={6} h={7} off={8}" -f (('  ' * $Depth) + '- '), $ct, $name, $aid, (To-Int $r.X), (To-Int $r.Y), (To-Int $r.Width), (To-Int $r.Height), $c.Current.IsOffscreen))
            $script:count++
        } catch {
            $lines.Add(('  ' * $Depth) + '- (读取失败)')
        }
        Dump2 ($c.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) ($Depth + 1)
    }
}

$root = Start-App
Set-Foreground
Start-Sleep -Seconds 1

$lines.Add('===== 窗口子树（跳过日志列表）=====')
Dump2 ($root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) 0

[IO.File]::WriteAllText($out, ($lines -join "`r`n"), (New-Object System.Text.UTF8Encoding($true)))
Write-Host "dumped -> $out  lines=$($lines.Count)"
exit 0
