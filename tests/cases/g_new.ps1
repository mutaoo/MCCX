# GUI 实测：2026-10-03 新增的「常用命令」按钮（手动添加 / 列表显示 / 删除）。
# 只看不改：测完把测试条目删掉，文件本来不存在就删文件，结束前收干净进程。
# 用法：pwsh -ExecutionPolicy Bypass -File tests\cases\g_new.ps1
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:GuiFail = 0
function Check([bool]$ok, [string]$name, [string]$detail = '') {
    if ($ok) { Write-Host "PASS  $name" }
    else { Write-Host "FAIL  $name  $detail"; $script:GuiFail++ }
}

# 弹层（Flyout/Popup）不挂在主窗口子树里，只能按 AutomationId + 进程跨窗口找
function Find-IdAnywhere([string]$Id) {
    $procId = Get-MainProcId
    if (-not $procId) { return $null }
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::AutomationIdProperty, $Id)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, $procId)))
    return $script:AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

$TestCmd = '/mccx-g-new-test'
$freqPath = Join-Path (Split-Path $script:AppExe) 'frequent-commands.json'
$hadFreqFile = Test-Path $freqPath

Write-Host '=== 启动 MCCX ==='
$appRoot = Restart-App
Start-Sleep -Seconds 3
if (-not $script:AppRoot) { $script:AppRoot = $appRoot }

try {
    Write-Host "`n=== 1) 「常用命令」按钮 ==="
    $btn = $null
    try { $btn = Get-UiButton '常用命令' } catch { Write-Host ("  " + $_.Exception.Message) }
    Check ($null -ne $btn) '命令栏左侧有「常用命令」按钮'

    Write-Host "`n=== 2) 展开下拉 ==="
    $box = $null
    if ($btn) {
        try {
            Invoke-Element $btn
            for ($i = 0; $i -lt 10 -and -not $box; $i++) {
                Start-Sleep -Milliseconds 300
                $box = Find-IdAnywhere 'NewFrequentCommandBox'
            }
        } catch { Write-Host ("  展开异常: " + $_.Exception.Message) }
    }
    Check ($null -ne $box) '下拉里有手动添加输入框'

    $hint = $null
    try { $hint = Find-IdAnywhere 'FrequentCommandsEmptyHint' } catch { }
    if ($btn -and $btn.Current.Name) {
        # 空列表时提示可见（有数据时隐藏，两种都算正常，只记录）
        Write-Host ("  空提示元素存在 = " + ($null -ne $hint))
    }

    if ($box) {
        Write-Host "`n=== 3) 手动添加一条 ==="
        try {
            Set-Edit $box $TestCmd
            $add = Find-UiElement $script:CT::Button '添加常用命令'
            Check ($null -ne $add) '找到「添加」按钮'
            if ($add) {
                Invoke-Element $add
                Start-Sleep -Milliseconds 700
                if (-not (Find-IdAnywhere 'NewFrequentCommandBox')) {
                    # 添加后如果下拉被关掉，重新打开继续验列表
                    $btn = Get-UiButton '常用命令'
                    Invoke-Element $btn
                    Start-Sleep -Milliseconds 700
                }
            }
        } catch {
            Check $false '添加常用命令' $_.Exception.Message
        }
    }

    Write-Host "`n=== 4) 列表里能看到这条 ==="
    $list = $null
    $texts = @()
    for ($i = 0; $i -lt 10 -and -not $list; $i++) {
        Start-Sleep -Milliseconds 300
        $list = Find-IdAnywhere 'FrequentCommandsList'
    }
    if ($list) {
        try {
            $texts = @(Get-ItemTexts $list)
            Write-Host ("  列表内容: " + ($texts -join ' | '))
        } catch { Write-Host ("  读列表异常: " + $_.Exception.Message) }
    }
    Check ($texts -contains $TestCmd) '常用命令列表包含刚添加的条目'

    Write-Host "`n=== 5) 删除这条（清掉测试数据）==="
    $del = $null
    if ($list) {
        try { $del = $list.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.AndCondition(
                    (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Button)),
                    (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, '删除常用命令'))))) } catch { }
    }
    Check ($null -ne $del) '条目上有删除叉号'
    if ($del) {
        try {
            Invoke-Element $del
            Start-Sleep -Milliseconds 700
            $after = @()
            $list2 = Find-IdAnywhere 'FrequentCommandsList'
            if ($list2) { $after = @(Get-ItemTexts $list2) }
            Check (-not ($after -contains $TestCmd)) '删除后条目消失' ("剩余=" + ($after -join ' | '))
        } catch {
            Check $false '删除常用命令' $_.Exception.Message
        }
    }

    Write-Host "`n=== 6) 命令输入框还在（历史键位挂在它上面）==="
    $cmdBox = $null
    try { $cmdBox = Find-ById $script:AppRoot 'CommandBox' } catch { }
    Check ($null -ne $cmdBox) '命令输入框存在'
    if ($cmdBox) {
        $ph = ''
        try { $ph = [string]$cmdBox.Current.Name } catch { }
        Write-Host ("  占位/名称 = [$ph]")
    }
}
finally {
    Write-Host "`n=== 收尾 ==="
    Stop-App
    # 测试数据不留痕：条目由上面的删除清掉；文件是这次测试新建的就整个删掉
    if (-not $hadFreqFile -and (Test-Path $freqPath)) {
        try { Remove-Item -Path $freqPath -Force } catch { }
        Write-Host "  已删除本次测试新建的 frequent-commands.json"
    }
}

Write-Host ("SUMMARY g_new FAILURES=$script:GuiFail")
exit $(if ($script:GuiFail -eq 0) { 0 } else { 1 })
