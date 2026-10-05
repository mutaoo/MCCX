# 验证 Flyout 内输入框 TwoWay 绑定：写入 -> 关闭 -> 重开 -> 读回
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

function Open-Flyout {
    param([string]$BtnName, [string]$WaitName = '攻击距离')
    $b = Get-UiButton $BtnName
    # 弹层偶尔要重试一次才出来（上一个弹层还没收完），等内容出现为准
    for ($i = 0; $i -lt 4; $i++) {
        try {
            $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) { $ec.Expand() }
        } catch { try { Invoke-Element $b } catch { } }
        Start-Sleep -Milliseconds 600
        try {
            $null = Get-UiEdit $WaitName
            return
        } catch { Start-Sleep -Milliseconds 400 }
    }
    throw "参数面板 [$BtnName] 打不开（等不到 $WaitName）"
}

function Close-Flyouts {
    Close-AllFlyouts
}

$null = Start-App
Save-Foreground

# 先把值归位成 3.0：弹层打开时键盘焦点会跟着弹层走，
# 用户此刻若在玩游戏按键（WASD/空格），可能被灌进这个框，这里做自愈。
Open-Flyout '自动砍怪选项'
Set-Edit (Get-UiEdit '攻击距离') '3.0'
$before = Get-EditValue (Get-UiEdit '攻击距离')
Write-Host "  before = $before"

Set-Edit (Get-UiEdit '攻击距离') '2.5'
Start-Sleep -Milliseconds 300
Close-Flyouts

Open-Flyout '自动砍怪选项'
$after = Get-EditValue (Get-UiEdit '攻击距离')
Close-Flyouts
Write-Host "  after  = $after"

Assert-True ($before -eq '3.0') '初始距离=3.0' "实际='$before'"
Assert-True ($after -eq '2.5') 'TwoWay 回写生效（重开面板读到 2.5）' "实际='$after'"

# 恢复默认值，避免影响后续
Open-Flyout '自动砍怪选项'
Set-Edit (Get-UiEdit '攻击距离') '3.0'
Close-Flyouts
Open-Flyout '自动砍怪选项'
$restored = Get-EditValue (Get-UiEdit '攻击距离')
Close-Flyouts
Assert-True ($restored -eq '3.0') '已恢复 3.0' "实际='$restored'"

$n = Get-FailureCount
Write-Host "FAILURES=$n"
Restore-Foreground
exit ([int]($n -gt 0))
