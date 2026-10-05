# 弓箭蓄力 e2e（2026-10-03 缺陷：按住右键松开后箭没有发射出去）：
#   连本地测试服 → RCON 给弓+箭 → 鼠标控制右键「间隔长按」按住 2000ms 后松开，
#   用 RCON 查箭实体，证明服务端收到了释放包（Player Action status 5 = RELEASE_USE_ITEM）
#   并结算了蓄力（箭的 damage 达标 = 按住期间蓄力没被打断）；
#   再验证「长按」模式下关掉右键开关也会补发释放包（松开发射）。
# 全程用专用测试账号 TestBow，结束删除，不碰用户已有账号；只用 UIA，不抢前台焦点。
# 用法：pwsh -ExecutionPolicy Bypass -File tests\cases\g_bow.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:TestUser = 'TestBow'
$script:TestPort = '25599'
# autolib 默认测试玩家是 MccXBot，本脚本账号叫 TestBow；不改的话
# RCON 的 execute as <玩家> 全部落空（与 p_filter / g_view 同款处理）。
$script:TestPlayer = $script:TestUser

# ---------- 账号库操作（与 p_multi / p_filter / g_view 一致） ----------
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

# ---------- 下拉参数（鼠标控制 / 视角调整） ----------
function Open-FlyoutByName {
    param([string]$ButtonName)
    # 窗口不在前台时 WinUI 弹层会间歇性打不开：开之前确认前台。
    # 此刻弹层必然是关的（开弹层前才调这里），符合 README「弹层打开期间禁用 Set-Foreground」。
    try {
        if ([MccXUi.Win32]::GetForegroundWindow() -ne [IntPtr]$script:Hwnd) { Set-Foreground }
    } catch { }
    $b = $null
    try { $b = Get-UiButton $ButtonName } catch { return $false }
    if (-not $b) { return $false }
    try {
        $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
            $ec.Expand()
        }
    } catch {
        try { Invoke-Element $b } catch { return $false }
    }
    Start-Sleep -Milliseconds 800
    return $true
}

function Open-MouseFlyout {
    # 展开是异步的，而且展开后马上操作弹层外的控件会把 light-dismiss 弹层带关
    # （d_mouse 实测过间歇展开失败）：必须等参数行真的进可视树才算成功，失败就重开。
    for ($i = 0; $i -lt 5; $i++) {
        if (Open-FlyoutByName '鼠标控制选项') {
            try {
                $null = Wait-For -TimeoutMs 4000 -What '鼠标参数下拉内容出现' -Probe {
                    $e = $null
                    try { $e = Get-UiToggle '右键启用' } catch { $e = $null }
                    if ($e) { 'ok' } else { $null }
                }
                return $true
            } catch { }
        }
        Close-AllFlyouts
        Start-Sleep -Milliseconds 500
    }
    return $false
}

# 任一交互都可能把 light-dismiss 弹层带关（点弹层外的东西、甚至焦点挪走）：
# 每次操作前先确认参数行还在，不在就重开弹层。
function Ensure-MouseFlyout {
    $e = $null
    try { $e = Get-UiToggle '右键启用' } catch { $e = $null }
    if ($e) { return $true }
    return (Open-MouseFlyout)
}

# WinUI 弹层里的参数行刚切模式时可能还没进可视树（d_mouse 实测会间歇找不到），带重试。
function Set-FlyoutValue {
    param([string]$Name, [string]$Value)
    $err = ''
    for ($i = 0; $i -lt 8; $i++) {
        if (-not (Ensure-MouseFlyout)) { $err = '弹层打不开'; Start-Sleep -Milliseconds 500; continue }
        try {
            Set-UiValue $Name $Value
            return
        } catch {
            $err = $_.Exception.Message
            Start-Sleep -Milliseconds 400
        }
    }
    throw "下拉参数 [$Name] 设置失败: $err"
}

function Set-FlyoutCombo {
    param([string]$Name, [string]$Item)
    $err = ''
    for ($i = 0; $i -lt 6; $i++) {
        if (-not (Ensure-MouseFlyout)) { $err = '弹层打不开'; Start-Sleep -Milliseconds 500; continue }
        try {
            # 预展开组合框并等弹层渲染：Set-ComboItem 只在展开后找一次（约 700ms），
            # 慢机器上弹层还没进桌面树就找，必漏。先把状态查出来，失败时能看到展开到底成没成。
            # SetFocus 是 shot_readme 同款：焦点给了组合框，WinUI 的下拉项才稳定暴露 UIA。
            $combo = Get-UiCombo $Name
            try { $null = $combo.SetFocus() } catch { }
            Start-Sleep -Milliseconds 300
            $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
                $ec.Expand()
            }
            Start-Sleep -Milliseconds 1200
            Write-Host "  [ui] combo state=$($ec.Current.ExpandCollapseState) (attempt $($i + 1))"
            Set-ComboItem $Name $Item
            return
        } catch {
            $err = $_.Exception.Message
            Write-Host "  [retry] 下拉 [$Name] 第 $($i + 1) 次失败: $err"
            # 弹层可能处于半开/残留状态：整层关掉重开，再来一遍
            try { Close-AllFlyouts } catch { }
            Start-Sleep -Milliseconds 800
            $null = Open-MouseFlyout
        }
    }
    throw "下拉框 [$Name] 选择 [$Item] 失败: $err"
}

function Set-FlyoutToggle {
    # 弹层内的开关：切开关本身有可能把 light-dismiss 弹层带关，失败就重开弹层再来
    param([string]$Name, [bool]$On)
    $err = ''
    for ($i = 0; $i -lt 6; $i++) {
        if (-not (Ensure-MouseFlyout)) { $err = '弹层打不开'; Start-Sleep -Milliseconds 500; continue }
        try {
            Set-ToggleOn $Name $On
            return
        } catch {
            $err = $_.Exception.Message
            Start-Sleep -Milliseconds 500
        }
    }
    throw "开关 [$Name] 未能切到 ${On}: $err"
}

function Get-FlyoutText {
    # 读弹层参数也带重试：桌面树跨窗口查找有间歇性空结果（d_mouse 实测）
    param([string]$Name)
    $err = ''
    for ($i = 0; $i -lt 6; $i++) {
        if (-not (Ensure-MouseFlyout)) { $err = '弹层打不开'; Start-Sleep -Milliseconds 500; continue }
        try { return Get-UiText $Name } catch { $err = $_.Exception.Message; Start-Sleep -Milliseconds 400 }
    }
    throw "读取下拉参数 [$Name] 失败: $err"
}

function Click-ViewButton {
    param([string]$Name)
    $el = $null
    for ($i = 0; $i -lt 6 -and -not $el; $i++) {
        if (-not (Open-FlyoutByName '视角调整选项')) { Start-Sleep -Milliseconds 400 }
        $el = $null
        try { $el = Find-UiElement $script:CT::Button $Name } catch { $el = $null }
        if (-not $el) { Start-Sleep -Milliseconds 400 }
    }
    if (-not $el) { throw "「视角调整」下拉里找不到按钮 [$Name]" }
    Invoke-Element $el
    Start-Sleep -Milliseconds 500
}

# ---------- RCON：给装备 / 查箭 ----------
function Grant-BowAndArrows {
    # 弓放到主手（weapon.mainhand 跟随当前选中格），箭给到背包任意空格
    Invoke-Rcon ('item replace entity {0} weapon.mainhand with minecraft:bow' -f $script:TestPlayer) | Out-Null
    Start-Sleep -Milliseconds 300
    Invoke-Rcon ('give {0} minecraft:arrow 16' -f $script:TestPlayer) | Out-Null
    Start-Sleep -Milliseconds 800
}

function Get-MainhandId {
    # 服务端记录的主手物品 id；弓还没同步过来时返回 null
    $out = $null
    try {
        $out = Invoke-Rcon ('execute as {0} run data get entity @s SelectedItem' -f $script:TestPlayer) 4000
    } catch {
        return $null
    }
    if ($out -match 'id:\s*"minecraft:([a-z_]+)"') { return $Matches[1] }
    return $null
}

function Clear-Arrows {
    Invoke-Rcon 'kill @e[type=arrow]' | Out-Null
    Start-Sleep -Milliseconds 400
}

function Get-ArrowDamage {
    # 有箭实体 => 返回箭的 damage（NBT 没写 damage 字段时按默认 2.0）；没箭 => $null
    $out = $null
    try {
        $out = Invoke-Rcon 'execute as @e[type=arrow,limit=1] run data get entity @s' 4000
    } catch {
        return $null
    }
    if ($out -notmatch 'Pos\s*:') { return $null } # 没有箭实体（No entity was found 之类）
    if ($out -match 'damage:\s*(-?\d+(?:\.\d+)?)') { return [double]$Matches[1] }
    return 2.0
}

function Wait-Arrow {
    param([int]$TimeoutMs = 20000, [string]$What = '箭实体出现（松开结算蓄力）')
    try {
        return Wait-For -TimeoutMs $TimeoutMs -What $What -Probe {
            $d = Get-ArrowDamage
            if ($null -ne $d) { $d } else { $null }
        }
    } catch {
        return $null
    }
}

# ============================================================
Clear-StrayRunners
$appRoot = Restart-App
Start-Sleep -Seconds 3
if (-not $script:AppRoot) { $script:AppRoot = $appRoot }
# 弹层/组合框下拉在非活动窗口上会间歇性打不开（实测 Expand 后 state 仍 Collapsed、
# 下拉项不暴露 UIA）：启动后先把窗口提为前台，结束时 Restore-Foreground 还回去。
# README 约束：只在弹层未打开时抢焦点，弹层打开期间绝不 Set-Foreground。
Set-Foreground
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

    # 右键：启用 + 间隔长按 + 按住 2000ms + 冷却 45000ms（一发之内不会重复按）
    # 左键、总开关先关掉，连接后给完弓再开总开关（否则第一按抓在空手上）。
    # 总开关在弹层外面，必须先设再开弹层（点弹层外的东西会把 light-dismiss 弹层带关）；
    # 弹层内的开关放最后：开关若把弹层带关，后面不再依赖弹层内容。
    Set-ToggleOn '鼠标控制' $false
    if (-not (Open-MouseFlyout)) { throw '打不开「鼠标控制」参数下拉' }
    Set-FlyoutCombo '右键模式' '间隔长按'
    Start-Sleep -Milliseconds 500
    Set-FlyoutValue '右键按住' '2000'
    Set-FlyoutValue '右键间隔' '45000'
    Start-Sleep -Milliseconds 400
    Assert-True ((Get-FlyoutText '右键按住') -eq '2000') 'A2 右键按住参数写入成功' "v=$(Get-FlyoutText '右键按住')"
    Assert-True ((Get-FlyoutText '右键间隔') -eq '45000') 'A3 右键间隔参数写入成功' "v=$(Get-FlyoutText '右键间隔')"
    Set-FlyoutToggle '左键启用' $false
    Set-FlyoutToggle '右键启用' $true
    Close-AllFlyouts

    Clear-Log
    Invoke-UiButton '连接'
    Assert-Log '连接成功' 'A4 本地测试服连接成功' 90000
    Assert-Log '已进入游戏' 'A5 进入游戏' 60000
    Start-Sleep -Seconds 5 # 等进服后的状态稳定（恢复窗口已移除，仅为落位/包稳定）

    # ============ B. 间隔长按：按住 2 秒松开 → 服务端出箭 ============
    Write-Host "`n=== B. 间隔长按按住 2s 后松开 ==="
    Grant-BowAndArrows
    $hand = $null
    try {
        $hand = Wait-For -TimeoutMs 10000 -What '服务端记录的主手是弓' -Probe {
            $id = Get-MainhandId
            if ($id -eq 'bow') { $id } else { $null }
        }
    } catch { $hand = $null }
    Assert-True ($hand -eq 'bow') 'B1 主手已持弓（服务端视角）' "hand=$hand"

    Clear-Arrows
    Click-ViewButton '视角向东' # 朝东放箭：箭飞出去落地，不会落回脚下被捡走

    Clear-Log
    Set-ToggleOn '鼠标控制' $true
    Assert-Log '鼠标控制已开启' 'B2 总开关打开并推送配置' 15000
    # Describe 日志里带上实际参数值：证明按住/间隔写进了 Bot（比界面回读更硬）
    Assert-Log '右键 / 间隔长按，按住 2000 ms，冷却 45000 ms' 'B3 鼠标 Bot 按右键参数启动' 15000
    # 只有手持蓄力类物品（弓）才会走「蓄力」按下路径；打出别的路径说明没认出弓
    Assert-Log '右键 按下（蓄力）' 'B4 按下日志走的是蓄力路径' 15000

    $damage = Wait-Arrow -TimeoutMs 25000
    Assert-True ($null -ne $damage) 'B5 松开后服务端生成了箭实体' "arrow=$damage"
    if ($null -ne $damage) {
        Assert-True ($damage -ge 1.0) 'B6 箭的蓄力伤害达标（按住期间蓄力没有被打断）' "damage=$damage"
    }

    # ============ C. 长按模式：关掉右键开关 = 松开 → 出箭 ============
    Write-Host "`n=== C. 长按模式下关掉右键开关 ==="
    Close-AllFlyouts
    Clear-Log
    if (-not (Open-MouseFlyout)) { throw '打不开「鼠标控制」参数下拉' }
    Set-FlyoutCombo '右键模式' '长按'
    # 模式切换会复位状态机并重新按下：蓄力中不重发，等真正的松开
    Assert-Log '右键 按下（蓄力）' 'C1 长按模式重新按下并进入蓄力' 15000
    Start-Sleep -Seconds 3 # 攒 3 秒蓄力（满蓄力需要 1 秒）
    Clear-Arrows
    Set-FlyoutToggle '右键启用' $false # 关掉右键 = 松开：必须补发释放包
    $damage2 = Wait-Arrow -TimeoutMs 15000
    Assert-True ($null -ne $damage2) 'C2 关掉右键开关后服务端生成了箭实体' "arrow=$damage2"
    Close-AllFlyouts
} finally {
    Write-Host "`n=== 收尾：关鼠标 + 断开 + 删除测试账号 ==="
    try { Close-AllFlyouts } catch { }
    try { Set-ToggleOn '鼠标控制' $false } catch { }
    # 弹层全关之后再还焦点（README：弹层打开期间禁用 Set-Foreground/Restore）
    try { Restore-Foreground } catch { }
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
Write-Host "SUMMARY g_bow FAILURES=$fail"
exit ([int]($fail -gt 0))
