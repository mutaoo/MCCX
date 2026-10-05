# 攻击生物过滤回归：分组候选列表 / 白名单黑名单 / 勾选 / 账号库持久化 / 真实打怪行为
# 全程用专用测试账号 TestFilter，结束后删除，不碰用户已有账号。
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:TestUser = 'TestFilter'
$script:TestPort = '25599'
$script:FilterModes = @('不过滤（仅敌对）', '白名单（只打勾选的）', '黑名单（勾选的不打）')
# autolib 默认把“测试玩家”当 MccXBot，本脚本账号是 TestFilter，
# 不改的话血量/创造/场地传送这类 execute as <玩家> 的 RCON 全部落空。
$script:TestPlayer = $script:TestUser

# ---------- 账号库操作（与 p_multi 一致） ----------
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

function Get-EditValueSafe {
    param([string]$Id)
    $e = Find-EditByIdSafe $Id
    if (-not $e) { return $null }
    return Get-EditValue $e
}

function Find-DialogCheckBox {
    param([string]$Name)
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::CheckBox)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)))
    return $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
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
        $v = Get-EditValueSafe 'UserBox'
        if ($v -eq $script:TestUser) { $v } else { $null }
    }
}

function Remove-ResidueAccount {
    # 上一轮异常退出可能留下残留账号。必须在“取基线”之前删干净，
    # 否则基线里混进残留，后面 A1/X1 的账号数对不上。
    if (-not (Find-ListItemByText (Get-AccountListEl) $script:TestUser)) { return }
    Write-Host '  [clean] 清理上轮残留的测试账号'
    Assert-True (Remove-AccountViaUi $script:TestUser) 'A0 残留账号删除走叉号+确认弹窗'
    $null = Wait-For -TimeoutMs 10000 -What '残留账号删除' -Probe {
        if (-not (Find-ListItemByText (Get-AccountListEl) $script:TestUser)) { 'gone' } else { $null }
    }
    Assert-True ($null -eq (Find-ListItemByText (Get-AccountListEl) $script:TestUser)) 'A0 残留测试账号已清理'
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

# ---------- 本地测试服（与 p_multi 一致） ----------
function Test-LocalServerListening {
    return [bool](netstat -ano | Select-String ":$($script:TestPort)\s.*LISTENING")
}

function Ensure-TestServer {
    $dir = "$(Join-Path $PSScriptRoot '..\artifacts')\mcserver\1.21.11"
    if (-not (Test-Path (Join-Path $dir 'server.jar'))) { throw "本地测试服缺失: $dir" }
    $props = Join-Path $dir 'server.properties'
    if (Test-Path $props) {
        Set-Content -Path $props -Value ((Get-Content $props) -replace '^server-port=.*$', "server-port=$($script:TestPort)") -Encoding ASCII
    }
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

function Setup-TestWorld {
    # 本地版世界准备：刻意不走 autolib 的 Setup-World/Ensure-PlayerAlive——
    # 它拿不到血量就会走 Ensure-Connected，把账号地址改回 25566/MccXBot 污染测试账号。
    $h = Get-PlayerHealth
    if ($h -le 0) {
        Send-MccCommand '/respawn'
        Start-Sleep -Seconds 2
        $h = Get-PlayerHealth
    }
    Assert-True ($h -gt 0) 'E4 玩家存活（RCON 能读到血量）' "health=$h"
    Assert-True (Set-PlayerGamemode 'creative') 'E5 创造模式已生效（RCON 回读校验）'
    $null = Set-ServerDifficulty 'easy'
    $null = Invoke-Rcon 'time set night'
    $null = Invoke-Rcon 'weather clear'
    Start-Sleep -Milliseconds 800
}

function Set-Arena {
    # 高空开阔平台 + 传送到位（同 Set-TestArena，但玩家名走本账号）
    $x = -345; $y = 110; $z = -443
    foreach ($dx in 0..3) {
        foreach ($dz in -1..1) {
            $null = Invoke-Rcon ("setblock {0} {1} {2} stone" -f ($x + $dx), ($y - 1), ($z + $dz))
        }
    }
    $out = Invoke-Rcon ("execute as {0} run tp @s {1} {2} {3} facing {4} {5} {6}" -f `
        $script:TestPlayer, $x, $y, $z, ($x + 1.0), ($y + 1.62), $z)
    Write-Host "  arena tp -> [$out]"
    Start-Sleep -Milliseconds 900
    $pos = Get-PlayerPos
    Assert-True ($null -ne $pos -and [math]::Abs($pos[1] - $y) -lt 1) 'E6 场地传送到位（RCON 与游戏同服）' "pos=$($pos -join ',')"
    return $pos
}

function Clear-StrayRunners {
    # runner 子进程只在账号删除 / 程序退出时才会被杀（断开不会），
    # 上一个套件（例如 p_port 连了 4 次但从不断开）留下的残留会干扰
    # “子进程已回收、仅剩主程序”这类断言。起手连主程序一起重启干净。
    $strays = @(Get-CimInstance Win32_Process -Filter "Name='MCCX.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '--runner' })
    if ($strays.Count -eq 0) { return }
    Write-Host ("  [clean] 清理残留 runner: " + (($strays | ForEach-Object { $_.ProcessId }) -join ','))
    Get-Process -Name MCCX -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    Start-Sleep -Seconds 2
}

function Summon-MobNear {
    param([string]$Id, [double]$Dist = 1.5)
    $pos = Get-PlayerPos
    if (-not $pos) { return $false }
    $out = Invoke-Rcon ("summon minecraft:{0} {1} {2} {3}" -f $Id, ($pos[0] + $Dist), [math]::Floor($pos[1]), $pos[2])
    Write-Host "  rcon summon $Id -> $out"
    return ($out -match 'Summoned')
}

# ---------- 过滤弹窗（入口：砍怪参数弹层里的“生物过滤”按钮） ----------
function Open-AttackFlyout {
    # 参数弹层的入口现在是“自动砍怪”标签本身（DropDownButton，点文字展开）
    $b = $null
    try { $b = Get-UiButton '自动砍怪选项' } catch { return $false }
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

function Find-NamedAny {
    # 按名字找控件、不限类型：生物过滤现在是参数弹层里的下拉按钮
    param([string]$Name)
    $nameCond = New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)
    $el = $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCond)
    if (-not $el) {
        $desktop = [System.Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object System.Windows.Automation.AndCondition(
            $nameCond,
            (New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, (Get-MainProcId))))
        $el = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
    }
    return $el
}

function Open-FilterDialog {
    # 生物过滤 = 砍怪参数弹层里的按钮，点了开模态弹窗（弹层同时被收起）
    for ($i = 0; $i -lt 6; $i++) {
        if (Test-FilterDialogOpen) { return $true }
        if (-not (Open-AttackFlyout)) {
            Start-Sleep -Milliseconds 500
            continue
        }
        $el = $null
        try { $el = Get-UiButton '生物过滤' } catch { $el = $null }
        if ($el) {
            try { Invoke-Element $el } catch { }
            Start-Sleep -Milliseconds 900
            if (Test-FilterDialogOpen) { return $true }
        }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

function Test-FilterDialogOpen {
    # 弹窗内容挂在主窗口子树里：拿“过滤模式”下拉当“弹窗开着”的判据
    return $null -ne (Find-UiElement $script:CT::ComboBox '过滤模式')
}

function Close-FilterDialog {
    if (-not (Test-FilterDialogOpen)) { return $true }
    $btn = $null
    try { $btn = Find-Button $script:AppRoot '关闭' } catch { $btn = $null }
    if (-not $btn) { $btn = Find-UiElement $script:CT::Button '关闭' }
    if ($btn) { try { Invoke-Element $btn } catch { } }
    $null = Wait-For -TimeoutMs 8000 -What '过滤弹窗关闭' -Probe {
        if (-not (Test-FilterDialogOpen)) { 'closed' } else { $null }
    }
    return (-not (Test-FilterDialogOpen))
}

function Set-CategoryExpanded {
    # 分类右侧的展开/收回按钮（ToggleButton）：只负责展开到 On，收起由调用方自己切
    param([string]$Name)
    $el = Find-NamedAny $Name
    if (-not $el) { throw "找不到分类展开按钮: $Name" }
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        $tp.Toggle()
        Start-Sleep -Milliseconds 450
    }
}

function Set-CategoryCollapsed {
    param([string]$Name)
    $el = Find-NamedAny $Name
    if (-not $el) { throw "找不到分类展开按钮: $Name" }
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        $tp.Toggle()
        Start-Sleep -Milliseconds 450
    }
}

function Test-LogContains {
    # Wait-LogContains 超时会抛异常，这里吞掉并返回 $false，好在循环里当条件用
    param([string]$Pattern, [int]$TimeoutMs = 9000)
    try {
        Wait-LogContains $Pattern $TimeoutMs | Out-Null
        return $true
    } catch {
        return $false
    }
}

function Get-CheckState {
    # 返回 $null=没找到 / $true=勾选 / $false=未勾选
    param([string]$Name)
    $el = Find-UiElement $script:CT::CheckBox $Name
    if (-not $el) { return $null }
    try {
        $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        return ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
    } catch { return $null }
}

function Set-CheckState {
    param([string]$Name, [bool]$On)
    $el = Find-UiElement $script:CT::CheckBox $Name
    if (-not $el) { throw "找不到复选框: $Name" }
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    for ($i = 0; $i -lt 6; $i++) {
        $now = ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
        if ($now -eq $On) { return }
        $tp.Toggle()
        Start-Sleep -Milliseconds 250
    }
    $now = ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
    if ($now -ne $On) { throw "复选框 [$Name] 未能切换到 $On" }
}

function Find-ListItemByPid {
    param([string]$Name)
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::ListItem)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, (Get-MainProcId))))
    return $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Get-ComboSelection {
    # 展开下拉，看哪个已知选项被选中（WinUI ComboBox 的 SelectionPattern 不可靠，走弹层）
    param([string]$ComboName)
    $combo = Get-UiCombo $ComboName
    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $ec.Expand()
    }
    Start-Sleep -Milliseconds 700
    try {
        foreach ($opt in $script:FilterModes) {
            $item = $null
            try { $item = Find-ComboPopupItem -ComboName $ComboName -ItemName $opt } catch { }
            if (-not $item) { $item = Find-ListItemByPid $opt }
            if (-not $item) { continue }
            try {
                $sip = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
                if ($sip.Current.IsSelected) { return $opt }
            } catch { }
        }
        return ''
    } finally {
        try {
            $ec2 = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($ec2.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
                $ec2.Collapse()
            }
        } catch { }
        Start-Sleep -Milliseconds 250
    }
}

$before = 0
$created = $false

try {
    Clear-StrayRunners
    $null = Start-App
    Save-Foreground
    Write-Host '--- app up ---'

    # ===== A. 建专用测试账号（不碰用户账号） =====
    Remove-ResidueAccount
    $before = Get-AccountCount
    Add-TestAccount
    $created = $true
    Assert-True ((Get-AccountCount) -eq ($before + 1)) 'A1 测试账号已添加' "count=$(Get-AccountCount) before=$before"
    Select-TestAccount
    Assert-True ((Get-EditValueSafe 'UserBox') -eq $script:TestUser) 'A2 选中测试账号面板'

    # ===== B. 过滤弹窗界面结构 =====
    Assert-True (Open-FilterDialog) 'B1 “生物过滤”弹窗可打开'
    $comboOk = $true
    try { $null = Get-UiCombo '过滤模式' } catch { $comboOk = $false }
    Assert-True $comboOk 'B2 含“模式”下拉'
    Assert-True ($null -ne (Find-UiElement $script:CT::Text '敌对生物（41）')) 'B3 敌对分组标题与数量'
    Assert-True ($null -ne (Find-UiElement $script:CT::Text '中立生物（13）')) 'B4 中立分组标题与数量'
    Assert-True ($null -ne (Find-UiElement $script:CT::Text '友好生物（37）')) 'B5 友好分组标题与数量'
    Assert-True ((Get-CheckState '僵尸') -eq $false) 'B6 新账号默认未勾选（僵尸）' "实际=$(Get-CheckState '僵尸')"
    Assert-True ($null -ne (Get-CheckState '蜘蛛')) 'B7 敌对候选可定位（蜘蛛，默认展开）'

    # 三个分类默认只展开敌对：中立/友好的生物要点分类右侧的展开按钮才出现
    Set-CategoryExpanded '中立展开'
    Assert-True ($null -ne (Get-CheckState '狼')) 'B8 中立候选可定位（狼，展开后）'
    Set-CategoryExpanded '友好展开'
    Assert-True ($null -ne (Get-CheckState '村民')) 'B9 友好候选可定位（村民，展开后）'
    $mode = Get-ComboSelection '过滤模式'
    Assert-True ($mode -eq $script:FilterModes[0]) 'B10 默认模式=不过滤（仅敌对）' "实际=$mode"

    # B11：分类右侧的展开/收回按钮来回切，该类生物跟着出现/消失
    Set-CategoryCollapsed '中立展开'
    Assert-True ($null -eq (Get-CheckState '狼')) 'B11a 收起中立分类后狼不可见'
    Set-CategoryExpanded '中立展开'
    Assert-True ($null -ne (Get-CheckState '狼')) 'B11b 再展开后狼又可见'

    # ===== C. 切白名单并勾选 =====
    Set-ComboItem '过滤模式' $script:FilterModes[1]
    Set-CheckState '僵尸' $true
    Set-CheckState '狼' $true
    Set-CheckState '村民' $true
    Assert-True ((Get-CheckState '僵尸') -eq $true) 'C1 勾选敌对生物（僵尸）'
    Assert-True ((Get-CheckState '狼') -eq $true) 'C2 勾选中立生物（狼）'
    Assert-True ((Get-CheckState '村民') -eq $true) 'C3 勾选友好生物（村民）'

    # C6-C10：分类标题右侧的“全选”框（整类勾选/取消，且只影响本分类）
    Set-CheckState '敌对生物全选' $true
    Assert-True ((Get-CheckState '蜘蛛') -eq $true) 'C6 敌对全选=整类勾上（蜘蛛）'
    Assert-True ((Get-CheckState '僵尸') -eq $true) 'C7 全选不冲掉原有勾选（僵尸）'
    Set-CheckState '敌对生物全选' $false
    Assert-True ((Get-CheckState '蜘蛛') -eq $false) 'C8 取消全选=整类全不选（蜘蛛）'
    Assert-True ((Get-CheckState '狼') -eq $true) 'C9 全选只作用于本分类（狼仍在）'
    Set-CheckState '僵尸' $true
    Assert-True ((Get-CheckState '僵尸') -eq $true) 'C10 恢复勾选（僵尸）'
    Assert-True (Close-FilterDialog) 'C11 过滤弹窗可关闭'

    # ===== D. 重启后随账号库恢复 =====
    $null = Restart-App
    Save-Foreground
    Select-TestAccount
    Assert-True (Open-FilterDialog) 'D1 重启后“生物过滤”弹窗可打开'
    $mode2 = Get-ComboSelection '过滤模式'
    Assert-True ($mode2 -eq $script:FilterModes[1]) 'D2 过滤模式已持久化' "实际=$mode2"
    Assert-True ((Get-CheckState '僵尸') -eq $true) 'D3 勾选名单已持久化（僵尸）'
    Set-CategoryExpanded '中立展开'
    Set-CategoryExpanded '友好展开'
    Assert-True ((Get-CheckState '狼') -eq $true) 'D4 勾选名单已持久化（狼）'
    Assert-True ((Get-CheckState '村民') -eq $true) 'D5 勾选名单已持久化（村民）'
    Assert-True (Close-FilterDialog) 'D6 重启后弹窗可关闭'

    # ===== E. 过滤参数走管道下发到子进程 =====
    Ensure-TestServer
    Clear-Log
    Invoke-UiButton '连接'
    Assert-Log '账号子进程已就绪' 'E1 子进程启动' 20000
    Assert-Log '连接成功' 'E2 连接本地测试服成功' 60000
    Assert-Log '已进入游戏' 'E3 进入世界' 60000

    Setup-TestWorld
    $null = Set-Arena

    Set-ToggleOn '自动砍怪' $true
    Assert-Log '\[砍怪\] 已开启' 'E7 砍怪 Bot 加载' 30000
    Assert-Log '白名单：只打勾选的 3 种生物' 'E8 过滤名单随连接下发到 Bot' 30000

    Assert-True (Open-FilterDialog) 'E9 运行中打开过滤弹窗'
    Set-ComboItem '过滤模式' $script:FilterModes[2]
    Assert-True (Close-FilterDialog) 'E9b 运行中可关闭过滤弹窗'
    Assert-Log '攻击过滤已更新（黑名单：勾选的 3 种生物不打' 'E10 运行中改模式实时生效' 20000

    # ===== F. 白名单：只打勾选的僵尸，牛不打 =====
    Assert-True (Open-FilterDialog) 'F1 打开过滤弹窗'
    Set-ComboItem '过滤模式' $script:FilterModes[1]
    Set-CategoryExpanded '中立展开'
    Set-CategoryExpanded '友好展开'
    Set-CheckState '狼' $false
    Set-CheckState '村民' $false
    Set-CheckState '僵尸' $true
    Assert-True (Close-FilterDialog) 'F1b 关闭过滤弹窗'
    Assert-Log '攻击过滤已更新（白名单：只打勾选的 1 种生物' 'F2 白名单只留僵尸并即时下发' 20000

    Clear-Log
    $atkZ = $false
    for ($i = 0; $i -lt 4 -and -not $atkZ; $i++) {
        $null = Summon-MobNear 'zombie' 1.5
        if (Test-LogContains '\[砍怪\] 攻击\s*(Zombie|僵尸)' 9000) { $atkZ = $true }
    }
    Assert-True $atkZ 'F3 白名单下勾选的僵尸被打'

    Assert-True (Summon-MobNear 'cow' 1.5) 'F4 召唤牛'
    Start-Sleep -Seconds 8
    $txt = Get-RecentLogText 80
    Assert-True ($txt -notmatch '\[砍怪\] 攻击\s*(Cow|牛)') 'F5 白名单下未勾选的牛不被打' (($txt -split "`n" | Select-Object -Last 5) -join ' | ')

    # ===== G. 黑名单：勾选的僵尸不打，其余全打 =====
    Assert-True (Open-FilterDialog) 'G1 打开过滤弹窗'
    Set-ComboItem '过滤模式' $script:FilterModes[2]
    Assert-True (Close-FilterDialog) 'G1b 关闭过滤弹窗'
    Assert-Log '攻击过滤已更新（黑名单：勾选的 1 种生物不打' 'G2 切黑名单并即时下发' 20000

    Clear-Log
    $null = Summon-MobNear 'cow' 1.5
    $atkC = $false
    for ($i = 0; $i -lt 4 -and -not $atkC; $i++) {
        if (Test-LogContains '\[砍怪\] 攻击\s*(Cow|牛)' 7000) { $atkC = $true } else { $null = Summon-MobNear 'cow' 1.5 }
    }
    Assert-True $atkC 'G3 黑名单下未勾选的牛被打'

    Clear-Log
    $null = Summon-MobNear 'zombie' 1.5
    Start-Sleep -Seconds 8
    $txt2 = Get-RecentLogText 80
    Assert-True ($txt2 -notmatch '\[砍怪\] 攻击\s*(Zombie|僵尸)') 'G4 黑名单下勾选的僵尸不被打' (($txt2 -split "`n" | Select-Object -Last 5) -join ' | ')
    # ===== H. 切账号后日志要停在最新一行（用户反馈：日志总显示在最上方） =====
    $logEl = Get-LogList
    $sp1 = $logEl.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    if (-not $sp1.Current.VerticallyScrollable) {
        # 日志不足一屏就没法验证滚动：用 RCON 广播刷几屏聊天行
        Write-Host '  [srv] 日志不足一屏，广播聊天行补足'
        for ($i = 1; $i -le 60; $i++) {
            try { $null = Invoke-Rcon "say scroll-test $i" } catch { break }
        }
        Start-Sleep -Milliseconds 2000
        $sp1 = $logEl.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
    }
    Assert-True ($sp1.Current.VerticallyScrollable) 'H1 日志长到可以滚动' "scrollable=$($sp1.Current.VerticallyScrollable)"

    # 横向传 -1(NoScroll)：本列表不横向滚动，横向给 0 会被 UIA 判为非法状态
    try { $sp1.SetScrollPercent(-1, 0) }
    catch { $sp1.SetScrollPercent([double]::NaN, 0) }
    Start-Sleep -Milliseconds 600
    $topPct = $sp1.Current.VerticalScrollPercent
    Assert-True ($topPct -lt 1) 'H2 已手动滚到顶部（模拟用户翻历史）' "pct=$topPct"

    # 切到左侧另一个账号，再切回测试账号
    $otherItem = $null
    foreach ($it in (Get-ListItems (Get-AccountListEl))) {
        $parts = @()
        foreach ($t in $it.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Text)))) {
            $parts += $t.Current.Name
        }
        if ((($parts -join ' / ')) -notmatch [regex]::Escape($script:TestUser)) { $otherItem = $it; break }
    }
    Assert-True ($null -ne $otherItem) 'H3 找到另一个账号用于切换' '列表里只有测试账号'

    if ($otherItem) {
        Select-Item $otherItem
        Start-Sleep -Milliseconds 1500
        Select-TestAccount
        Start-Sleep -Milliseconds 2000

        $sp2 = $logEl.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
        $afterPct = 100
        if ($sp2.Current.VerticallyScrollable) { $afterPct = $sp2.Current.VerticalScrollPercent }
        Assert-True ($afterPct -ge 99) 'H4 切回账号后日志落在底部最新一行' "pct=$afterPct"
    }
}
catch {
    Assert-True $false '用例执行异常' $_.Exception.Message
    Write-Host ("EXCEPTION: " + $_.Exception.Message)
}
finally {
    try {
        # 弹窗/弹层是模态的，先把它们关干净，后面的开关、断开、删账号才点得动
        try { $null = Close-FilterDialog } catch { }
        try { Close-AllFlyouts } catch { }
        try { Set-ToggleOn '自动砍怪' $false } catch { }
        try { Disconnect-App } catch { }
        try { $null = Set-ServerDifficulty 'peaceful' } catch { }

        if ($created) {
            # 新版删除 = 账号项右侧叉号 + 二次确认弹窗；没点到就重启一次再试，
            # 绝不能把测试账号留在账号库里。
            $done = $false
            try { $done = Remove-AccountViaUi $script:TestUser } catch { $done = $false }
            if (-not $done) {
                try {
                    $null = Restart-App
                    Save-Foreground
                    $done = Remove-AccountViaUi $script:TestUser
                } catch { $done = $false }
            }

            if ($done) {
                $null = Wait-For -TimeoutMs 10000 -What '测试账号删除' -Probe {
                    if (-not (Find-ListItemByText (Get-AccountListEl) $script:TestUser)) { 'gone' } else { $null }
                }
                Assert-True ((Get-AccountCount) -eq $before) 'X1 测试账号已删除、账号数复原' "count=$(Get-AccountCount) before=$before"
                Assert-True ($null -eq (Find-ListItemByText (Get-AccountListEl) $script:TestUser)) 'X2 账号库无测试残留'
            } else {
                Assert-True $false 'X1 未能通过叉号+确认弹窗删除测试账号（账号库可能残留 TestFilter）'
            }
        }
    } catch {
        Assert-True $false '清理阶段异常' $_.Exception.Message
    }
    Restore-Foreground
}

# 收尾：子进程回收 + 重启后测试账号不复活
try {
    $null = Wait-For -TimeoutMs 25000 -What '子进程回收' -Probe {
        $n = @(Get-Process -Name MCCX -ErrorAction SilentlyContinue).Count
        if ($n -eq 1) { $n } else { $null }
    }
    Write-Host 'X3 子进程已回收（仅剩主程序）OK'
    $null = Restart-App
    Save-Foreground
    Assert-True ($null -eq (Find-ListItemByText (Get-AccountListEl) $script:TestUser)) 'X4 重启后测试账号未复活'
    Restore-Foreground
} catch {
    Assert-True $false '收尾检查异常' $_.Exception.Message
}

$fc = Get-FailureCount
Write-Host "P_FILTER FAILURES = $fc"
exit ([int]($fc -gt 0))
