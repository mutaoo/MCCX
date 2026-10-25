# 自动指令里的 /useitem（2026-10-23 用户报：“日志说已执行，背包里物品还在”）。
#   根因：MCC 命令栏的 /useitem 在开着地形处理时是“瞄准方块就放方块”（射线 → PlaceBlock），
#   手上是末影珍珠 / 食物 / 烟花这类非方块物品时服务端不收，物品不消耗，但命令照样返回成功。
#   修复：自动指令里的 /useitem（不带坐标）直接发“使用物品”包（与 AutoEat 同一条通路）。
# 本用例在玩家正前方 2 格放一块石头（射线必中），副手放 3 颗末影珍珠：
#   A 旧行为下会走 PlaceBlock（珍珠不消耗）；现在必须真的用掉珍珠（3 → 2）并且人被传送走。
#   B 对照：把挡路的方块去掉（射线打空）也必须能用 —— 证明修复不是只在某一条分支上生效。
#   C 重启程序后（参数落盘）不影响：这里只看 A/B，避免和 g_autocmd 的落盘用例重复。
# 全程专用测试账号 TestItem，结束删除；只用 UIA + RCON，不抢前台焦点。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tests\cases\g_useitem.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:TestUser = 'TestItem'
$script:TestPort = '25599'
$script:TestPlayer = $script:TestUser

function Find-EditByIdSafe {
    param([string]$Id)
    try { return Find-ById $script:AppRoot $Id } catch { return $null }
}

function Find-DialogCheckBox {
    param([string]$Name)
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::CheckBox)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)))
    return $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Add-TestAccount {
    Invoke-UiButton '添加账号'
    $null = Wait-For -TimeoutMs 10000 -What '添加账号弹窗出现' -Probe {
        if (Find-EditByIdSafe 'NewServerBox') { 'open' } else { $null }
    }

    $cb = $null
    for ($i = 0; $i -lt 10 -and -not $cb; $i++) {
        $cb = Find-DialogCheckBox '立即连接'
        if (-not $cb) { Start-Sleep -Milliseconds 300 }
    }
    if ($cb) {
        $tp = $cb.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) {
            $tp.Toggle()
            Start-Sleep -Milliseconds 300
        }
    }

    Set-Edit (Find-Edit $script:AppRoot 'NewServerBox') '127.0.0.1'
    Set-Edit (Find-Edit $script:AppRoot 'NewPortBox') $script:TestPort
    Set-Edit (Find-Edit $script:AppRoot 'NewUserBox') $script:TestUser
    Set-Edit (Find-Edit $script:AppRoot 'NewVersionBox') 'auto'

    $addBtn = Find-Button $script:AppRoot '添加'
    if (-not $addBtn) { throw '找不到弹窗的 [添加] 按钮' }
    Invoke-Element $addBtn
    $null = Wait-For -TimeoutMs 10000 -What '添加账号弹窗关闭' -Probe {
        if (Find-EditByIdSafe 'NewServerBox') { $null } else { 'closed' }
    }
}

function Remove-ResidueAccount {
    if (-not (Find-ListItemByText (Get-AccountListEl) $script:TestUser)) { return }
    Write-Host "  [clean] 删除残留账号 $($script:TestUser)"
    $null = Remove-AccountViaUi $script:TestUser
    $null = Wait-For -TimeoutMs 15000 -What '残留账号删除' -Probe {
        if (-not (Find-ListItemByText (Get-AccountListEl) $script:TestUser)) { 'gone' } else { $null }
    }
}

function Select-TestAccount {
    $item = $null
    for ($i = 0; $i -lt 6 -and -not $item; $i++) {
        $item = Find-ListItemByText (Get-AccountListEl) $script:TestUser
        if (-not $item) { Start-Sleep -Milliseconds 400 }
    }
    if (-not $item) { throw "账号列表里找不到 $($script:TestUser)" }
    Select-Item $item
    $null = Wait-For -TimeoutMs 8000 -What '面板切到测试账号' -Probe {
        $v = $null
        try { $v = Get-EditValue (Find-Edit $script:AppRoot 'UserBox') } catch { $v = $null }
        if ($v -eq $script:TestUser) { $v } else { $null }
    }
}

# ---------- 本地测试服 ----------
function Test-LocalServerListening {
    return [bool](netstat -ano | Select-String ":$($script:TestPort)\s.*LISTENING")
}

function Ensure-TestServer {
    $dir = "$(Join-Path $PSScriptRoot '..\artifacts')\mcserver\26.1"
    if (-not (Test-Path (Join-Path $dir 'server.jar'))) { throw "本地测试服缺失: $dir" }
    if (Test-LocalServerListening) {
        Write-Host "  [srv] 本地测试服已在运行 127.0.0.1:$($script:TestPort)"
        return
    }
    $java = Get-TestServerJava
    Start-Process -FilePath $java -ArgumentList '-Xmx1G', '-jar', 'server.jar', 'nogui' `
        -WorkingDirectory $dir -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $dir 'test.out.log') `
        -RedirectStandardError (Join-Path $dir 'test.err.log')
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Seconds 2
        if (Test-LocalServerListening) {
            Write-Host "  [srv] 本地测试服已启动 127.0.0.1:$($script:TestPort)"
            return
        }
    }
    throw "本地测试服启动超时（$($script:TestPort) 未监听）"
}

function Clear-StrayRunners {
    $strays = @(Get-CimInstance Win32_Process -Filter "Name='MCCX.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '--runner' })
    if ($strays.Count -eq 0) { return }
    Write-Host ("  [clean] 清理残留 runner: " + (($strays | ForEach-Object { $_.ProcessId }) -join ','))
    Get-Process -Name MCCX -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    Start-Sleep -Seconds 2
}

# ---------- 自动指令弹层（与 g_autocmd 同一套锚点）----------
function Show-AutoCmdPanel {
    $el = $null
    try { $el = Get-UiButton '自动指令选项' } catch { }
    if (-not $el) { throw '找不到「自动指令选项」按钮' }

    $w = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $node = $el
    for ($i = 0; $i -lt 10 -and $null -ne $node; $i++) {
        try {
            $sp = $node.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            $sp.SetScrollPercent(100, -1)
            Start-Sleep -Milliseconds 300
            break
        } catch { }
        $node = $w.GetParent($node)
    }

    $btn = Get-UiButton '自动指令选项'
    $ec = $btn.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $ec.Expand()
        Start-Sleep -Milliseconds 600
    }
}

function Hide-AutoCmdPanel {
    try {
        $btn = Get-UiButton '自动指令选项'
        $ec = $btn.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded) {
            $ec.Collapse()
            Start-Sleep -Milliseconds 400
        }
    } catch { }
}

function Get-NamedElements {
    param([string]$Name, $Type)
    $procId = Get-MainProcId
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $Type)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, $procId)))
    return @($script:AE::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c))
}

function Get-RowTexts { return (Get-NamedElements '自动指令文本' $script:CT::Edit) }
function Get-RowIntervals { return (Get-NamedElements '自动指令间隔' $script:CT::Edit) }
function Get-RowModes { return (Get-NamedElements '自动指令方式' $script:CT::ComboBox) }

function Wait-RowCount {
    param([int]$Expect, [int]$TimeoutMs = 8000)
    $deadline = [Environment]::TickCount64 + $TimeoutMs
    while ([Environment]::TickCount64 -lt $deadline) {
        $n = (Get-RowTexts).Count
        if ($n -eq $Expect) { return $n }
        Start-Sleep -Milliseconds 200
    }
    return (Get-RowTexts).Count
}

function Set-RowMode {
    param($Combo, [string]$ItemName)
    $ec = $Combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $ec.Expand()
    }
    Start-Sleep -Milliseconds 700
    try {
        $picked = Find-ComboPopupItem -ComboName '自动指令方式' -ItemName $ItemName
        if (-not $picked) { throw "下拉项 [$ItemName] 未找到" }
        Select-Item $picked
        Start-Sleep -Milliseconds 400
    } finally {
        try { $ec.Collapse() } catch { }
        Start-Sleep -Milliseconds 250
    }
}

function Set-FeatureToggle {
    param([string]$Name, [bool]$On)
    $el = Get-UiToggle $Name
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $isOn = ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
    if ($isOn -ne $On) {
        $tp.Toggle()
        Start-Sleep -Milliseconds 500
    }
    return ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
}

# ---------- 服务器侧道具布置 ----------
function Reset-PlayerForItemTest {
    # 生存模式才会消耗道具；先清空背包，再把珍珠放进快捷栏第 1 格（= 当前选中格，实测 SelectedItemSlot=0）
    Invoke-Rcon ('gamemode survival {0}' -f $script:TestPlayer) | Out-Null
    Invoke-Rcon ('clear {0}' -f $script:TestPlayer) | Out-Null
    Invoke-Rcon ('effect give {0} minecraft:instant_health 1 20 true' -f $script:TestPlayer) | Out-Null
    Invoke-Rcon ('tp {0} -6.5 72.0 6.5 0 0' -f $script:TestPlayer) | Out-Null
    Invoke-Rcon ('item replace entity {0} hotbar.0 with minecraft:ender_pearl 3' -f $script:TestPlayer) | Out-Null
}

function Place-BlockInFront {
    # ^ ^ ^2 = 玩家朝向正前方 2 格；抬高 1.5 格让方块落在视线高度上（射线从眼睛出发）
    Invoke-Rcon ('execute at {0} run setblock ^ ^1.5 ^2 minecraft:stone' -f $script:TestPlayer) | Out-Null
}

function Clear-BlockInFront {
    Invoke-Rcon ('execute at {0} run setblock ^ ^1.5 ^2 minecraft:air' -f $script:TestPlayer) | Out-Null
}

function Get-PearlCount {
    # clear <玩家> <物品> 0 = 只数不删：有就回 "Found N matching item(s)"，没有回 "No items were found"。
    # 不用 data get entity Inventory：实测副手物品不在那份列表里（1.21.9+），会误判成 0。
    $out = ''
    try { $out = Invoke-Rcon ('clear {0} minecraft:ender_pearl 0' -f $script:TestPlayer) 5000 } catch { return -1 }
    $m = [regex]::Match($out, 'Found\s+(\d+)\s+matching')
    if ($m.Success) { return [int]$m.Groups[1].Value }
    if ($out -match 'No items were found') { return 0 }
    return -1
}

function Get-PlayerPosYaw {
    try {
        $out = Invoke-Rcon ('data get entity {0} Pos' -f $script:TestPlayer) 5000
        return $out
    } catch {
        return ''
    }
}

function Wait-PearlCount {
    param([int]$Expect, [int]$TimeoutMs = 20000)
    $deadline = [Environment]::TickCount64 + $TimeoutMs
    $last = -2
    while ([Environment]::TickCount64 -lt $deadline) {
        $last = Get-PearlCount
        if ($last -eq $Expect) { return $last }
        Start-Sleep -Milliseconds 500
    }
    return $last
}

# ============================================================
Clear-StrayRunners
$appRoot = Restart-App
Start-Sleep -Seconds 3
if (-not $script:AppRoot) { $script:AppRoot = $appRoot }
$before = Get-AccountCount
Write-Host "--- app up (accounts=$before) ---"

try {
    Ensure-TestServer
    Remove-ResidueAccount
    $before = Get-AccountCount

    Add-TestAccount
    $now = Wait-AccountCount -Expect ($before + 1) -TimeoutMs 10000
    Assert-True ($now -eq ($before + 1)) 'A1 测试账号已添加' "count=$now before=$before"

    Select-TestAccount
    Clear-Log
    Invoke-UiButton '连接'
    Assert-Connected 'A2 本地测试服连接成功' 90000
    Assert-Log '已进入游戏' 'A3 进入游戏' 60000
    Start-Sleep -Seconds 2

    # 新建一条 /useitem：间隔 2 秒 + 只执行一次（方便数“确实只发了一次”）
    Show-AutoCmdPanel
    Invoke-UiButton '新建自动指令'
    $rows = Wait-RowCount -Expect 1
    Assert-True ($rows -eq 1) 'A4 新建后弹层里出现一行' "rows=$rows"
    Set-Edit (Get-RowTexts)[0] '/useitem'
    Set-Edit (Get-RowIntervals)[0] '2'
    Set-RowMode (Get-RowModes)[0] '只执行一次'
    Hide-AutoCmdPanel

    # ============ A. 正前方有方块（旧代码会走 PlaceBlock，珍珠不消耗） ============
    Write-Host "`n=== A. 视线正前方有方块：/useitem 必须真的用掉道具 ==="
    Reset-PlayerForItemTest
    Start-Sleep -Milliseconds 800
    Place-BlockInFront
    Start-Sleep -Milliseconds 800
    Write-Host '  [srv] 已清背包 + 主手 3 颗末影珍珠 + 正前方放石头'
    $c0 = Get-PearlCount
    Assert-True ($c0 -eq 3) 'A5 前置：主手有 3 颗末影珍珠' "count=$c0"
    $pos0 = Get-PlayerPosYaw

    Clear-Log
    Set-FeatureToggle '自动指令' $true | Out-Null
    Assert-Log '自动指令已开启' 'A6 自动指令总开关已打开' 15000
    Assert-Log '[自动指令]' 'A7 自动指令执行了 /useitem' 20000

    Write-Host '  [srv] 等珍珠被消耗…'
    $c1 = Wait-PearlCount -Expect 2 -TimeoutMs 20000
    Assert-True ($c1 -eq 2) 'A8 末影珍珠被真正使用（3 → 2）' "count=$c1"
    $pos1 = Get-PlayerPosYaw
    Assert-True ($pos0 -ne $pos1) 'A9 玩家被珍珠传送走（效果确实发生了）' "pos0=$pos0 pos1=$pos1"

    Set-FeatureToggle '自动指令' $false | Out-Null
    Start-Sleep -Milliseconds 500

    # ============ B. 对照：视线前方没有方块也必须能用 ============
    Write-Host "`n=== B. 视线打空时同样能用（修复没有只覆盖一条分支） ==="
    Reset-PlayerForItemTest
    Start-Sleep -Milliseconds 800
    Clear-BlockInFront
    Start-Sleep -Milliseconds 800
    $c2 = Get-PearlCount
    Assert-True ($c2 -eq 3) 'B1 前置：又发了 3 颗珍珠' "count=$c2"

    Clear-Log
    Set-FeatureToggle '自动指令' $true | Out-Null
    Assert-Log '[自动指令]' 'B2 自动指令再次执行 /useitem' 20000
    Write-Host '  [srv] 等珍珠被消耗…'
    $c3 = Wait-PearlCount -Expect 2 -TimeoutMs 20000
    Assert-True ($c3 -eq 2) 'B3 打空视线时珍珠同样被消耗（3 → 2）' "count=$c3"
    Set-FeatureToggle '自动指令' $false | Out-Null
    Start-Sleep -Milliseconds 500

    # ============ C. 命令栏直接敲 /useitem（用户截图里那条通路） ============
    # 用户 2026-10-24 反馈："有的账号窗口能正常用，有的没有" —— 走的就是这里：
    # MCC 的 /useitem 先射线找方块，看着方块就 PlaceBlock（珍珠这类非方块服务端不收），
    # 所以同一个命令在不同的视角/窗口下表现不一样。
    Write-Host "`n=== C. 命令栏敲 /useitem（与用户截图同一条路） ==="
    Reset-PlayerForItemTest
    Start-Sleep -Milliseconds 800
    Place-BlockInFront
    Start-Sleep -Milliseconds 800
    $c4 = Get-PearlCount
    Assert-True ($c4 -eq 3) 'C1 前置：主手 3 颗珍珠 + 视线正前方有方块' "count=$c4"

    Clear-Log
    Send-MccCommand '/useitem'
    Assert-Log '使用了一个物品' 'C2 命令栏 /useitem 回了 MCC 的成功提示' 10000
    Write-Host '  [srv] 等珍珠被消耗…'
    $c5 = Wait-PearlCount -Expect 2 -TimeoutMs 20000
    Assert-True ($c5 -eq 2) 'C3 命令栏 /useitem 也真的用掉了珍珠（3 → 2）' "count=$c5"

    # ============ D. 手上是空的：要明确提示，不再假装成功 ============
    Write-Host "`n=== D. 选中格是空的：给出明确提示 ==="
    Invoke-Rcon ('clear {0}' -f $script:TestPlayer) | Out-Null
    Start-Sleep -Milliseconds 800
    $c6 = Get-PearlCount
    Assert-True ($c6 -eq 0) 'D1 前置：背包已清空' "count=$c6"

    Clear-Log
    Send-MccCommand '/useitem'
    Assert-Log '手上没有物品' 'D2 空手时明确提示，而不是只说“用了一个物品”' 10000
} finally {
    Write-Host "`n=== 收尾：断开 + 删除测试账号 ==="
    try { Disconnect-App } catch { }
    try { Invoke-Rcon ('clear {0}' -f $script:TestPlayer) | Out-Null } catch { }
    try {
        if (Find-ListItemByText (Get-AccountListEl) $script:TestUser) {
            $null = Remove-AccountViaUi $script:TestUser
            $null = Wait-For -TimeoutMs 20000 -What '测试账号删除' -Probe {
                if (-not (Find-ListItemByText (Get-AccountListEl) $script:TestUser)) { 'gone' } else { $null }
            }
        }
    } catch {
        Write-Host ("  [clean] 删除测试账号失败: " + $_.Exception.Message)
    }
    $after = Get-AccountCount
    if ($after -ne $before) {
        $after = Wait-AccountCount -Expect $before -TimeoutMs 10000
    }
    Assert-True ($after -eq $before) 'X1 账号数已恢复（未污染用户账号库）' "after=$after before=$before"
}

Write-Host "`n=== 收尾：关闭程序 ==="
Stop-App
$fail = Get-FailureCount
Write-Host "SUMMARY g_useitem FAILURES=$fail"
exit ([int]($fail -gt 0))
