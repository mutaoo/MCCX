# 空账号列表：右侧应为空白页；从零添加/删除账号；结束后把用户账号文件原样放回
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$exeDir = "$((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) + '\MCCX.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64')"
$stash = "$(Join-Path $PSScriptRoot '..\artifacts')\accounts-stash"
$appDataDirs = @("$env:APPDATA\MCCX", "$env:APPDATA\MccX")
# 2026-10-05：账号库与配置类文件统一在程序目录的 UserData 子目录里
$dataDir = Join-Path $exeDir 'UserData'
$files = @('accounts.dat', 'accounts.key')

function Find-EditByIdSafe {
    param([string]$Id)
    $e = $null
    try { $e = Find-ById $script:AppRoot $Id } catch { $e = $null }
    return $e
}

function Get-AccountListEl {
    $l = Find-ById $script:AppRoot 'AccountList'
    if (-not $l) { throw '找不到账号列表 AccountList' }
    return $l
}

function Get-AccountCount { return @(Get-ListItems (Get-AccountListEl)).Count }

function Hide-AccountFiles {
    if (Test-Path $stash) { Remove-Item $stash -Recurse -Force }
    New-Item -ItemType Directory -Path $stash | Out-Null
    $i = 0
    foreach ($n in $files) {
        $src = Join-Path $dataDir $n
        if (Test-Path $src) { Copy-Item $src (Join-Path $stash $n) -Force; Remove-Item $src -Force }
    }
    foreach ($d in $appDataDirs) {
        $i++
        if (Test-Path $d) { Move-Item $d "$stash-appdata$i" -Force }
    }
}

function Restore-AccountFiles {
    foreach ($n in $files) {
        $s = Join-Path $stash $n
        $d = Join-Path $dataDir $n
        if (Test-Path $s) {
            if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir | Out-Null }
            Copy-Item $s $d -Force
        }
    }
    $i = 0
    foreach ($d in $appDataDirs) {
        $i++
        $s = "$stash-appdata$i"
        if (Test-Path $s) {
            if (Test-Path $d) { Remove-Item $d -Recurse -Force }
            Move-Item $s $d -Force
        }
    }
}

# 先记录用户当前账号数（还原后要和它比对），再停程序
$backupCount = -1
try {
    $null = Start-App
    $backupCount = Get-AccountCount
    Write-Host "  备份前账号数 = $backupCount"
} catch {
    Write-Host "  ! 读取备份前账号数失败: $($_.Exception.Message)"
}

Stop-App
$stashCreated = $false

try {
    Hide-AccountFiles
    $stashCreated = $true

    # ============ 1. 空列表：右侧空白页 ============
    $null = Start-App
    Save-Foreground

    $n = Get-AccountCount
    Assert-True ($n -eq 0) 'H1 启动时账号列表为空' "count=$n"

    Assert-True ($null -eq (Find-EditByIdSafe 'ServerBox')) 'H2 无账号时右侧面板隐藏（空白页）'
    Assert-True ($null -eq (Find-EditByIdSafe 'StateTextBlock')) 'H3 无账号时状态条也隐藏'
    $null = Get-UiButton '添加账号'
    Write-Host 'H4 空白页上 [添加账号] 按钮仍可用 OK'

    # ============ 2. 空白页上从零添加一个账号 ============
    Invoke-Element (Get-UiButton '添加账号')
    $null = Wait-For -TimeoutMs 10000 -What '弹窗出现' -Probe {
        if (Find-EditByIdSafe 'NewServerBox') { 'open' } else { $null }
    }
    Set-Edit (Find-EditByIdSafe 'NewServerBox') '127.0.0.1'
    Set-Edit (Find-EditByIdSafe 'NewPortBox') '25599'
    Set-Edit (Find-EditByIdSafe 'NewUserBox') 'BlankTest'
    # 取消“立即连接”
    $cb = $null
    for ($i = 0; $i -lt 10 -and -not $cb; $i++) {
        $c = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::CheckBox)),
            (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, '立即连接')))
        $cb = $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if (-not $cb) { Start-Sleep -Milliseconds 300 }
    }
    $tp = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ([string]$tp.Current.ToggleState -eq 'On') { $tp.Toggle() }
    Start-Sleep -Milliseconds 300

    Invoke-Element (Find-Button $script:AppRoot '添加')
    $null = Wait-For -TimeoutMs 10000 -What '弹窗关闭' -Probe {
        if (Find-EditByIdSafe 'NewServerBox') { $null } else { 'closed' }
    }

    Assert-True ((Get-AccountCount) -eq 1) 'H5 添加后列表里有 1 个账号' "count=$(Get-AccountCount)"
    $srv = Wait-For -TimeoutMs 8000 -What '右侧面板出现' -Probe {
        $e = Find-EditByIdSafe 'ServerBox'; if ($e) { (Get-EditValue $e) } else { $null }
    }
    Assert-True ($srv -eq '127.0.0.1') 'H6 右侧面板显示新账号（不再是空白页）' "server=$srv"
    $stateEl = Find-EditByIdSafe 'StateTextBlock'
    Assert-True (($stateEl -and $stateEl.Current.Name -eq '未连接')) 'H7 未勾选立即连接则保持未连接' "state=$(if ($stateEl) { $stateEl.Current.Name } else { '<无>' })"

    # ============ 3. 删掉它 → 回到空白页 ============
    $item = Find-ListItemByText (Get-AccountListEl) 'BlankTest'
    Assert-True ($null -ne $item) 'H8 列表里能看到 BlankTest'
    Assert-True (Remove-AccountViaUi 'BlankTest') 'H8b 删除走叉号+确认弹窗'
    $null = Wait-For -TimeoutMs 10000 -What '删空' -Probe {
        if ((Get-AccountCount) -eq 0) { 'empty' } else { $null }
    }
    Assert-True ((Get-AccountCount) -eq 0) 'H9 删除最后一个账号后列表为空'
    Assert-True ($null -eq (Find-EditByIdSafe 'ServerBox')) 'H10 删空后右侧回到空白页'
}
finally {
    # 无论成败，都必须把用户的账号文件原样放回去
    Stop-App
    if ($stashCreated) { Restore-AccountFiles }
    Write-Host '--- 账号文件已还原 ---'
}

# ============ 4. 还原后重启，用户的账号完好（数量与测试前一致） ============
$null = Start-App
Save-Foreground
$n2 = Get-AccountCount
if ($backupCount -ge 0) {
    Assert-True ($n2 -eq $backupCount) 'H11 还原后用户账号数与测试前一致' "count=$n2 before=$backupCount"
} else {
    Assert-True ($n2 -ge 1) 'H11 还原后用户账号非空' "count=$n2"
}
Assert-True ($null -ne (Find-EditByIdSafe 'ServerBox')) 'H12 还原后右侧面板正常显示'

Restore-Foreground

$fail = Get-FailureCount
Write-Host "FAILURES=$fail"
exit ([int]($fail -gt 0))
