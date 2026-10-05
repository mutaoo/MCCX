# 自动补充 e2e（2026-10-03 第五批需求 2：手持物品用完时自动从背包补同款）：
#   连本地测试服 → 主手放 1 块土 + 背包第 12 格放 1 块土 → MCC 命令只丢主手：
#   A（开关关）：主手掉空后不会自己补（负向对照，顺带证明 drop 命令真的把主手清空了）；
#   B（开关开）：掉空后服务端看到主手又被补上土、背包第 12 格那块被取走、日志有补货行；
#   C（背包没同款）：手上木棍掉空后不补，并打出「背包里没有同款」日志；
#   D（重连认领/清退，修 2026-10-04 重复挂载 bug）：
#     D1 开着断开重连 → 不打第二条「已就绪」（没挂两个）且认领的实例照常补货；
#     D2 断线期间关掉 → 重连后掉空不补（恢复的旧实例已被卸掉，没有僵尸挂载）。
# 搜索范围按用户确认：主背包优先 + 其他快捷栏兜底（本用例只用到主背包来源）。
# 全程专用测试账号 TestRefill，结束删除，不碰用户已有账号；只用 UIA + RCON，不抢前台焦点。
# 用法：pwsh -ExecutionPolicy Bypass -File tests\cases\g_refill.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:TestUser = 'TestRefill'
$script:TestPort = '25599'
# autolib 默认测试玩家是 MccXBot，本脚本账号叫 TestRefill；不改的话
# RCON 的 execute as <玩家> 全部落空（与 p_filter / g_view / g_bow 同款处理）。
$script:TestPlayer = $script:TestUser

# ---------- 账号库操作（与 p_multi / p_filter / g_view / g_bow 一致） ----------
function Get-AccountListEl {
    $l = Find-ById $script:AppRoot 'AccountList'
    if (-not $l) { throw '找不到账号列表 AccountList' }
    return $l
}

function Get-AccountCount { return @(Get-ListItems (Get-AccountListEl)).Count }

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
    # 上一轮异常退出可能留下残留账号；必须在取基线之前删干净。
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
    $dir = "$(Join-Path $PSScriptRoot '..\artifacts')\mcserver\1.21.11"
    if (-not (Test-Path (Join-Path $dir 'server.jar'))) { throw "本地测试服缺失: $dir" }
    if (Test-LocalServerListening) {
        Write-Host "  [srv] 本地测试服已在运行 127.0.0.1:$($script:TestPort)"
        return
    }
    $java = (Get-Command java -ErrorAction SilentlyContinue).Source
    if (-not $java) { throw 'java 不在 PATH，无法启动本地测试服' }
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

# ---------- RCON：放土 / 查主手 / 查背包第 12 格 ----------
function Give-Mainhand {
    param([string]$Item)
    # weapon.mainhand 跟随当前选中格（新会话默认选中 0 = 协议槽 36）
    Invoke-Rcon ('item replace entity {0} weapon.mainhand with {1}' -f $script:TestPlayer, $Item) | Out-Null
    Start-Sleep -Milliseconds 300
}

function Give-BackupDirt {
    # inventory.11 = 玩家背包第 12 格（协议槽 20）：补货来源就用它，能顺带验证取货槽位
    Invoke-Rcon ('item replace entity {0} inventory.11 with minecraft:dirt' -f $script:TestPlayer) | Out-Null
    Start-Sleep -Milliseconds 300
}

function Get-MainhandId {
    # 服务端记录的主手物品 id；空手（或查询失败）返回 null
    $out = $null
    try {
        $out = Invoke-Rcon ('execute as {0} run data get entity @s SelectedItem' -f $script:TestPlayer) 4000
    } catch {
        return $null
    }
    if ($out -match 'id:\s*"minecraft:([a-z_]+)"') { return $Matches[1] }
    return $null
}

function Get-BackupDirtSlot {
    # 背包第 12 格（Inventory 里的 Slot:20b）物品 id；空格返回 $null
    $out = $null
    try {
        $out = Invoke-Rcon ('execute as {0} run data get entity @s Inventory' -f $script:TestPlayer) 4000
    } catch {
        return $null
    }
    if ($out -match '\{[^{}]*Slot:\s*20b[^{}]*\}') {
        $entry = $Matches[0]
        if ($entry -match 'id:\s*"minecraft:([a-z_]+)"') { return $Matches[1] }
        return '?'
    }
    return $null
}

function Drop-Mainhand {
    # MCC 内部命令：只丢协议槽 36（= 主手），背包里那块土不动
    Send-MccCommand '/inventory player drop 36 all'
}

function Write-RecentLog {
    # 断言失败时把最近日志打出来：/inventory 命令的报错也会在里面
    try {
        $txt = Get-RecentLogText
        if ($txt) {
            $lines = @($txt -split "`r?`n")
            $tail = ($lines | Select-Object -Last 8) -join ' | '
            Write-Host "  [dbg] 最近日志: $tail"
        }
    } catch { }
}

# ============================================================
Clear-StrayRunners
$appRoot = Restart-App
Start-Sleep -Seconds 3
if (-not $script:AppRoot) { $script:AppRoot = $appRoot }
$before = Get-AccountCount
Write-Host "--- app up (accounts=$before) ---"

try {
    # ============ A. 起服 + 建测试账号 + 连接 ============
    Ensure-TestServer
    Remove-ResidueAccount
    $before = Get-AccountCount

    Add-TestAccount
    Assert-True ((Get-AccountCount) -eq ($before + 1)) 'A1 测试账号已添加' "count=$(Get-AccountCount) before=$before"

    Select-TestAccount

    Clear-Log
    Invoke-UiButton '连接'
    Assert-Log '连接成功' 'A2 本地测试服连接成功' 90000
    Assert-Log '已进入游戏' 'A3 进入游戏' 60000
    Start-Sleep -Seconds 2

    Assert-True (-not (Get-ToggleOn '自动补充')) 'A4 「自动补充」默认关' "on=$(Get-ToggleOn '自动补充')"

    # ============ B. 开关关着：主手掉空不会自己补（负向对照） ============
    Write-Host "`n=== A. 开关关着：主手掉空不补 ==="
    Give-Mainhand 'minecraft:dirt'
    Give-BackupDirt
    Start-Sleep -Seconds 2 # 给服务端/客户端把两块土都落定
    Clear-Log
    Drop-Mainhand
    $gone = $null
    try {
        $gone = Wait-For -TimeoutMs 8000 -What '主手掉空（drop 命令生效）' -Probe {
            $id = Get-MainhandId
            if ($null -eq $id) { 'empty' } else { $null }
        }
    } catch { $gone = $null }
    Assert-True ($null -ne $gone) 'A5 主手已掉空（drop 命令生效，后续断言才有意义）' "hand=$(Get-MainhandId)"
    if ($null -eq $gone) { Write-RecentLog }
    Start-Sleep -Seconds 2 # 留出“假如会自己补”的时间窗
    Assert-True ($null -eq (Get-MainhandId)) 'A6 关着时主手保持空（不会自己补）' "hand=$(Get-MainhandId)"
    Assert-True ((Get-BackupDirtSlot) -eq 'dirt') 'A7 背包第 12 格的土还在（只丢了主手）' "slot20=$(Get-BackupDirtSlot)"
    if ((Get-BackupDirtSlot) -ne 'dirt') {
        # 排查：把服务端的 Inventory NBT 原样打出来，确认 item replace 放到了哪一格 / 输出格式
        try {
            $raw = Invoke-Rcon ('execute as {0} run data get entity @s Inventory' -f $script:TestPlayer) 4000
            Write-Host "  [dbg] Inventory NBT: $raw"
        } catch {
            Write-Host ("  [dbg] Inventory NBT 查询失败: " + $_.Exception.Message)
        }
    }

    # ============ C. 打开开关：掉空 → 自动补上 ============
    Write-Host "`n=== B. 打开开关：掉空自动补 ==="
    Clear-Log
    Set-ToggleOn '自动补充' $true
    Assert-Log '自动补充已开启' 'B1 开关打开并推送配置' 15000
    # 排查点：Bot 就绪日志必须出现在这次 Clear-Log 之前（B1 之后），否则后面“完全没日志”
    # 无法区分「Bot 没挂上」和「掉空没被发现」。挂不上 = B1b 失败，挂上了 = 看掉空后的补货日志。
    Assert-Log '已就绪：手持物品用完后自动从背包补同款' 'B1b 自动补充 Bot 已就绪' 15000

    Give-Mainhand 'minecraft:dirt' # 手上再放一块，掉空才算“用完”
    Start-Sleep -Seconds 2         # Bot 基线落定：记住手里有土
    Clear-Log
    Drop-Mainhand
    $back = $null
    try {
        $back = Wait-For -TimeoutMs 12000 -What '主手被自动补上土' -Probe {
            $id = Get-MainhandId
            if ($id -eq 'dirt') { $id } else { $null }
        }
    } catch { $back = $null }
    Assert-True ($back -eq 'dirt') 'B2 掉空后服务端看到主手被补上土' "hand=$back"
    if ($null -ne $back) {
        Assert-Log '已从背包第 12 格补到手上' 'B3 日志记录补货行（来源 = 背包第 12 格）' 8000
        Assert-True ($null -eq (Get-BackupDirtSlot)) 'B4 背包第 12 格那块土被取走（补货来源正确）' "slot20=$(Get-BackupDirtSlot)"
    } else {
        Write-RecentLog
        Assert-True $false 'B3 日志记录补货行（来源 = 背包第 12 格）' '(B2 失败，跳过)'
        Assert-True $false 'B4 背包第 12 格那块土被取走（补货来源正确）' '(B2 失败，跳过)'
    }

    # ============ D. 背包没有同款：不补 + 提示日志 ============
    Write-Host "`n=== C. 背包没有同款：不补 ==="
    Give-Mainhand 'minecraft:stick' # 背包里没有任何木棍
    Start-Sleep -Seconds 2
    Clear-Log
    Drop-Mainhand
    Start-Sleep -Seconds 3 # 明确的“不补”观察窗
    Assert-True ($null -eq (Get-MainhandId)) 'C1 没同款时主手保持空（不乱补别的东西）' "hand=$(Get-MainhandId)"
    Assert-Log '背包里没有同款' 'C2 给出“没有同款”提示日志' 8000

    # ============ D. 重连认领/清退（2026-10-04 重复挂载 bug 回归） ============
    # 断线时 Bot 被 MCC 放进静态 botsOnHold（不走 OnUnload），重连原样恢复挂载，而会话字段已置空。
    # D1（开着断开重连）：必须认领恢复的实例——再挂一个的话重连后会打出第二条「已就绪」；
    # D2（断线期间关掉）：必须卸掉恢复的实例——否则旧实例照跑，掉空还会被补上。
    Write-Host "`n=== D. 重连认领/清退（重复挂载回归） ==="

    # ---- D1: 开着断开重连：不重复挂载，认领的实例照常工作 ----
    Disconnect-App
    Clear-Log
    Invoke-UiButton '连接'
    Assert-Log '连接成功' 'D1a 重连成功' 90000
    Assert-Log '已进入游戏' 'D1b 重连进入游戏' 60000
    Start-Sleep -Seconds 5 # 给 ApplyAutomation/认领留时间窗
    $recent = [string](Get-RecentLogText)
    Assert-True ($recent -notmatch [regex]::Escape('已就绪：手持物品用完后自动从背包补同款')) `
        'D1c 没有重复挂载（重连不打第二条 Bot 就绪日志）' (($recent -split "`n" | Select-Object -Last 6) -join ' | ')
    Assert-True (Get-ToggleOn '自动补充') 'D1d 重连后开关仍是开的（参数保持）' "on=$(Get-ToggleOn '自动补充')"

    # 认领的实例必须真的在干活（防“认领到了但没跑”）
    Give-BackupDirt
    Give-Mainhand 'minecraft:dirt'
    Start-Sleep -Seconds 2 # 让 Bot 对新放上的土建立基线
    Clear-Log
    Drop-Mainhand
    $back2 = $null
    try {
        $back2 = Wait-For -TimeoutMs 12000 -What '重连后主手被自动补上土' -Probe {
            if ((Get-MainhandId) -eq 'dirt') { 'dirt' } else { $null }
        }
    } catch { $back2 = $null }
    Assert-True ($back2 -eq 'dirt') 'D1e 重连认领的实例照常补货（没挂上/没跑都能被这步抓住）' "hand=$back2"
    if ($null -eq $back2) { Write-RecentLog }

    # ---- D2: 断线期间关掉：重连必须卸掉恢复的旧实例（没有僵尸挂载） ----
    Disconnect-App
    Set-ToggleOn '自动补充' $false # 断开状态下也可以切（只写会话配置/参数）
    Clear-Log
    Invoke-UiButton '连接'
    Assert-Log '连接成功' 'D2a 关闭后再重连成功' 90000
    Assert-Log '已进入游戏' 'D2b 关闭后再重连进入游戏' 60000
    Start-Sleep -Seconds 5
    Assert-True (-not (Get-ToggleOn '自动补充')) 'D2c 重连后开关仍是关的' "on=$(Get-ToggleOn '自动补充')"

    Give-BackupDirt # 假如旧实例照跑，这就是它的“补货来源”
    Give-Mainhand 'minecraft:dirt'
    Start-Sleep -Seconds 2
    Clear-Log
    Drop-Mainhand
    $gone2 = $null
    try {
        $gone2 = Wait-For -TimeoutMs 8000 -What '主手掉空（drop 命令生效）' -Probe {
            if ($null -eq (Get-MainhandId)) { 'empty' } else { $null }
        }
    } catch { $gone2 = $null }
    Assert-True ($null -ne $gone2) 'D2d 主手已掉空（drop 命令生效）' "hand=$(Get-MainhandId)"
    Start-Sleep -Seconds 3 # 明确的“不补”观察窗
    Assert-True ($null -eq (Get-MainhandId)) 'D2e 关着时重连不补货（恢复的旧实例已被卸掉）' "hand=$(Get-MainhandId)"
    $recent2 = [string](Get-RecentLogText)
    Assert-True ($recent2 -notmatch [regex]::Escape('已从背包第 12 格补到手上')) 'D2f 日志里没有补货行' `
        (($recent2 -split "`n" | Select-Object -Last 6) -join ' | ')
    if ($null -ne (Get-MainhandId)) { Write-RecentLog }
} finally {
    Write-Host "`n=== 收尾：关自动补充 + 断开 + 删除测试账号 ==="
    try { Set-ToggleOn '自动补充' $false } catch { }
    try { Disconnect-App } catch { }
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
    Assert-True ($after -eq $before) 'X1 账号数已恢复（未污染用户账号库）' "after=$after before=$before"
}

Write-Host "`n=== 收尾：关闭程序 ==="
Stop-App
$fail = Get-FailureCount
Write-Host "SUMMARY g_refill FAILURES=$fail"
exit ([int]($fail -gt 0))
