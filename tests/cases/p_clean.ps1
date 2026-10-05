# 清理 p_multi 中断后残留的测试账号 TestMulti（走正常删除流程，验证账号库同步）
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

function Get-AccountListEl {
    $l = Find-ById $script:AppRoot 'AccountList'
    if (-not $l) { throw '找不到账号列表 AccountList' }
    return $l
}

function Get-AccountCount { return @(Get-ListItems (Get-AccountListEl)).Count }

$null = Start-App
Save-Foreground
Write-Host "清理前账号数 = $(Get-AccountCount)"
Write-Host "MCCX 进程数 = $(@(Get-Process MCCX -ErrorAction SilentlyContinue).Count)"

$removed = 0
for ($i = 0; $i -lt 5; $i++) {
    $item = $null
    try { $item = Find-ListItemByText (Get-AccountListEl) 'TestMulti' } catch { $item = $null }
    if (-not $item) { break }
    $null = Remove-AccountViaUi 'TestMulti'
    $null = Wait-For -TimeoutMs 10000 -What 'TestMulti 被删除' -Probe {
        if (-not (Find-ListItemByText (Get-AccountListEl) 'TestMulti')) { 'gone' } else { $null }
    }
    $removed++
}

$n = Get-AccountCount
Write-Host "已删除 $removed 个，剩余账号数 = $n"
foreach ($it in (Get-ListItems (Get-AccountListEl))) {
    $parts = @()
    foreach ($t in $it.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Text)))) {
        $parts += $t.Current.Name
    }
    Write-Host ("  - " + (($parts -join ' | ')))
}
Write-Host "MCCX 进程数 = $(@(Get-Process MCCX -ErrorAction SilentlyContinue).Count)"
Restore-Foreground
exit 0
