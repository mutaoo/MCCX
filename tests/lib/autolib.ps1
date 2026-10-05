# MccX 自动化测试公共库：UIA 操作 + RCON 控制测试服
# 约定：所有提示用 Write-Host（不进管道），函数只 return 真正的返回值，
#       否则 $x = Fn 会拿到 Object[] 导致 Assert-True 报类型错误。
# 注意：本文件含中文，必须以 UTF-8 with BOM 保存。
# 控制台输出统一 UTF-8：否则中文经管道回传会变成乱码
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\uia.ps1"

$script:AppRoot = $null
$script:LogListEl = $null
$script:AppExe = "$((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) + '\MCCX.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64')\MCCX.exe"
$script:RconHost = '127.0.0.1'
$script:RconPort = 25575
$script:RconPass = 'mccxtest'
$script:TestPlayer = 'MccXBot'
$script:Failures = New-Object System.Collections.Generic.List[string]

function Assert-True {
    param($Condition, [string]$Name, [string]$Detail = '')
    $ok = [bool]$Condition
    if ($ok) {
        Write-Host "PASS  $Name"
    } else {
        $script:Failures.Add($Name)
        Write-Host "FAIL  $Name  $Detail"
    }
}

function Get-FailureCount { return $script:Failures.Count }

function Stop-App {
    $ps = @(Get-Process -Name MCCX -ErrorAction SilentlyContinue)
    foreach ($p in $ps) { try { $p.Kill() } catch { } }

    # 必须等进程真的没了再返回：固定睡 2 秒不够时，Start-App 会看到“还有进程在跑”
    # 于是不启新实例，随后的窗口等待必然超时（等待 MCCX 窗口超时）。
    for ($i = 0; $i -lt 30; $i++) {
        if (-not (Get-Process -Name MCCX -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Milliseconds 500

    $script:AppRoot = $null
    $script:LogListEl = $null
}

function Start-App {
    param([int]$TimeoutMs = 30000)
    if (-not (Get-Process -Name MCCX -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $script:AppExe -WorkingDirectory (Split-Path $script:AppExe)
    }
    $script:AppRoot = Wait-AppWindow -TimeoutMs $TimeoutMs
    $script:LogListEl = $null
    Start-Sleep -Seconds 2
    return $script:AppRoot
}

function Restart-App {
    Stop-App
    return Start-App
}

# ---------- UI 定位 ----------

function Find-UiElement {
    # 主窗口子树找不到时回退到桌面树：Flyout/下拉弹层挂在 PopupWindowSiteBridge 下，
    # 不属于主窗口子树，必须按 进程 + 控件类型 + 名称 跨窗口精确定位。
    param($ControlType, [string]$Name)
    $procId = Get-MainProcId
    if (-not $procId) { return $null }
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $ControlType)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ProcessIdProperty, $procId)))
    return $script:AE::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Get-MainProcId {
    # 多开时同一 exe 有多个进程（每个账号一个 --runner 子进程），
    # 只能用“带主窗口的那个”当过滤条件，取 First 1 会拿到子进程 pid，导致什么都查不到。
    try {
        if ($script:AppRoot) { return [int]$script:AppRoot.Current.ProcessId }
    } catch { }
    $p = Get-Process -Name MCCX -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($p) { return [int]$p.Id }
    return 0
}

function Get-UiButton {
    param([string]$Name)
    $b = Find-Button $script:AppRoot $Name
    if (-not $b) { $b = Find-UiElement $script:CT::Button $Name }
    if (-not $b) { throw "找不到按钮: $Name" }
    return $b
}

function Get-UiToggle {
    param([string]$Name)
    $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::Button)
    $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    $c = New-Object System.Windows.Automation.AndCondition($c1, $c2)
    $el = $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if (-not $el) { $el = Find-UiElement $script:CT::Button $Name }
    if (-not $el) { throw "找不到开关: $Name" }
    $null = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return $el
}

function Get-UiEdit {
    param([string]$Name)
    $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::Edit)
    $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    $c = New-Object System.Windows.Automation.AndCondition($c1, $c2)
    $el = $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if (-not $el) { $el = Find-UiElement $script:CT::Edit $Name }
    if (-not $el) { throw "找不到输入框: $Name" }
    return $el
}

function Get-UiCombo {
    param([string]$Name)
    $c1 = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::ComboBox)
    $c2 = New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $Name)
    $c = New-Object System.Windows.Automation.AndCondition($c1, $c2)
    $el = $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
    if (-not $el) { $el = Find-UiElement $script:CT::ComboBox $Name }
    if (-not $el) { throw "找不到下拉框: $Name" }
    return $el
}

function Set-UiValue {
    param([string]$Name, [string]$Value)
    $el = Get-UiEdit $Name
    Set-Edit $el $Value
}

function Get-UiText {
    param([string]$Name)
    $el = Get-UiEdit $Name
    return Get-EditValue $el
}

# ---------- 按钮 ----------

function Invoke-UiButton {
    # 带“按钮可用性等待 + 重定位 + 报错定位”的安全点击
    param([string]$Name, [int]$WaitEnabledMs = 15000, [bool]$Optional = $false)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $lastErr = ''
    while ($sw.ElapsedMilliseconds -lt $WaitEnabledMs) {
        $b = Get-UiButton $Name
        if ($b.Current.IsEnabled) {
            try {
                Write-Host "  [ui] click '$Name'"
                Invoke-Element $b
                return
            } catch {
                $lastErr = $_.Exception.Message
                Write-Host "  [ui] click '$Name' raised: $lastErr"
            }
        } else {
            $lastErr = 'IsEnabled=false'
        }
        Start-Sleep -Milliseconds 300
    }
    if ($Optional) {
        Write-Host "  [ui] optional click '$Name' skipped ($lastErr)"
        return
    }
    throw "按钮 [$Name] 无法点击: $lastErr"
}

# ---------- 开关 ----------

function Get-ToggleOn {
    param([string]$Name)
    $el = Get-UiToggle $Name
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
}

function Set-ToggleOn {
    param([string]$Name, [bool]$On)
    $el = Get-UiToggle $Name
    # 不抢焦点：UIA TogglePattern 不需要前台，抢了会把用户正在输入的按键灌进本程序
    $tp = $el.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    for ($i = 0; $i -lt 6; $i++) {
        $now = ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
        if ($now -eq $On) { return }
        $tp.Toggle()
        Start-Sleep -Milliseconds 250
    }
    $now = ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On)
    if ($now -ne $On) { throw "开关 [$Name] 未能切换到 $On" }
}

# 用 UIA 关掉所有已展开的参数下拉。
# 不用 SendKeys ESC：那要求本程序在前台，否则 ESC 会发给用户正在玩的游戏窗口。
function Close-AllFlyouts {
    if (-not $script:AppRoot) { return }
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Button)
    $btns = $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($b in $btns) {
        try {
            $ec = $b.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($ec.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::Expanded) {
                $ec.Collapse()
            }
        } catch { }
    }
    Start-Sleep -Milliseconds 400
}

# ---------- 下拉框 ----------
# 实测：WinUI ComboBox 展开后，选项在独立的 PopupWindowSiteBridge Pane 里，
# 主窗口子树找不到，必须从桌面树按 ClassName + Name 定位弹层里的 ListItem。

function Find-ComboPopupItem {
    param([string]$ComboName, [string]$ItemName)

    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $itemCond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::ListItem)),
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $ItemName)))

    $paneCond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'Microsoft.UI.Content.PopupWindowSiteBridge')),
        (New-Object System.Windows.Automation.PropertyCondition($AE::NameProperty, $ComboName)))
    $pane = $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $paneCond)
    if ($pane) {
        $hit = $pane.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
        if ($hit) { return $hit }
    }

    $paneCond2 = New-Object System.Windows.Automation.PropertyCondition($AE::ClassNameProperty, 'Microsoft.UI.Content.PopupWindowSiteBridge')
    $panes = $desktop.FindAll([System.Windows.Automation.TreeScope]::Descendants, $paneCond2)
    foreach ($p in $panes) {
        $hit = $p.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $itemCond)
        if ($hit) { return $hit }
    }

    $pidCond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, (Get-MainProcId))
    $whole = New-Object System.Windows.Automation.AndCondition($itemCond, $pidCond)
    return $desktop.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $whole)
}

function Set-ComboItem {
    param([string]$ComboName, [string]$ItemName)
    $combo = Get-UiCombo $ComboName
    # 不抢焦点（同 Set-ToggleOn）

    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $ec.Expand()
    }
    Start-Sleep -Milliseconds 700

    try {
        $picked = Find-ComboPopupItem -ComboName $ComboName -ItemName $ItemName
        if (-not $picked) {
            throw "下拉项 [$ItemName] 在弹层中未找到"
        }
        Select-Item $picked
        Start-Sleep -Milliseconds 300

        $sip = $picked.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        if (-not $sip.Current.IsSelected) {
            throw "下拉框 [$ComboName] 选择 [$ItemName] 未生效 (IsSelected=false)"
        }
        Write-Host "  [ui] combo '$ComboName' -> '$ItemName'"
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

# ---------- 账号删除（新版流程） ----------

function Remove-AccountViaUi {
    # 删除流程已改版：底部“删除”按钮移除，改成账号项右侧的叉号（AutomationId 同模板，
    # 名称=“删除账号”）+ ContentDialog 二次确认。返回 $false 表示该账号已不在列表里。
    param([string]$Name, [int]$TimeoutMs = 20000)

    $list = Find-ById $script:AppRoot 'AccountList'
    if (-not $list) { throw '找不到账号列表 AccountList' }

    $item = Find-ListItemByText $list $Name
    if (-not $item) { return $false }

    Select-Item $item
    Start-Sleep -Milliseconds 500

    # 叉号就在这个账号项的模板里，绝不能按名字全局找（否则会点到别的账号）
    $btn = $null
    for ($i = 0; $i -lt 10 -and -not $btn; $i++) {
        $btn = $item.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Button)),
                (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, '删除账号')))))
        if (-not $btn) { Start-Sleep -Milliseconds 250 }
    }
    if (-not $btn) { throw "账号项 [$Name] 里找不到删除叉号（删除账号）" }

    Invoke-Element $btn

    # 二次确认弹窗：主按钮文案“删除”
    $ok = $null
    for ($i = 0; $i -lt [Math]::Max(8, $TimeoutMs / 250) -and -not $ok; $i++) {
        Start-Sleep -Milliseconds 250
        $ok = Find-UiElement $script:CT::Button '删除'
    }
    if (-not $ok) { throw "点叉号后没有弹出删除确认窗口（账号 $Name）" }

    # 弹窗必须是这个账号的：账号名出现在正文里。对不上就绝不能按“删除”，
    # 否则会误删别的账号（这条同时能揪出“叉号点错行”这类问题）。
    $dlg = $null
    foreach ($t in $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Text)))) {
        $n = [string]$t.Current.Name
        if ($n.Contains('确定删除账号')) { $dlg = $n; break }
    }
    if ($null -eq $dlg) { throw "删除确认弹窗读不到正文（账号 $Name）" }
    if (-not $dlg.Contains($Name)) {
        throw "删除确认弹窗与目标账号不符，拒绝确认：目标=$Name 弹窗=$dlg"
    }

    Invoke-Element $ok
    return $true
}

# ---------- 日志 ----------

function Get-LogList {
    # 2026-10-05：日志容器从 ListView 换成了 ScrollViewer(LogScrollHost) + ItemsRepeater
    # （ListView 会在行内抢指针，导致行内 TextBox 拖不出选区）。
    # 注意 **ItemsRepeater 自己没有 UIA 节点**（WinUI 不给它做 automation peer），
    # 按 AutomationId=LogList 找会扑空；它的父 ScrollViewer 有节点，而且日志行（Edit）
    # 都在它下面，所以这里返回 LogScrollHost，找不到再退回 LogList。
    if (-not $script:LogListEl) {
        $script:LogListEl = Find-ById $script:AppRoot 'LogScrollHost'
        if (-not $script:LogListEl) {
            $script:LogListEl = Find-ById $script:AppRoot 'LogList'
        }
        if (-not $script:LogListEl) { throw '找不到日志区域（LogScrollHost / LogList）' }
    }
    return $script:LogListEl
}

function Get-RecentLogText {
    param([int]$Max = 60)
    $log = Get-LogList

    # 2026-10-05：日志容器从 ListView 换成 ItemsRepeater（为了让行内 TextBox 能正常框选文字，
    # ListView 会在行内抢指针做自己的选中）。于是：
    #   · 滚动宿主是显式的 ScrollViewer（AutomationId=LogScrollHost），先滚到底再读；
    #   · 条目不再是 ListItem，改为收集所有 Edit（每行一个只读 TextBox）并按 ValuePattern 取文本。
    try {
        $host = Find-ById $script:AppRoot 'LogScrollHost'
        if ($host) {
            $sp = $host.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if ($sp.Current.VerticallyScrollable) { $sp.SetScrollPercent(-1, 100) }
        }
    } catch { }
    Start-Sleep -Milliseconds 250

    $cEdit = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $script:CT::Edit)
    $edits = $log.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cEdit)
    $lines = @()
    foreach ($e in $edits) {
        $v = ''
        try {
            $vp = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
            $v = [string]$vp.Current.Value
        } catch { $v = [string]$e.Current.Name }
        if ($v) { $lines += $v }
    }

    # 万一一个 Edit 都没找到（容器结构再变），退回按条目读，保底不把用例整体带崩
    if ($lines.Count -eq 0) { $lines = @(Get-ItemTexts $log) }

    if ($lines.Count -gt $Max) {
        $lines = $lines[($lines.Count - $Max)..($lines.Count - 1)]
    }
    return ($lines -join "`n")
}

function Clear-Log {
    Invoke-UiButton '清空日志'
    Start-Sleep -Milliseconds 400
}

function Wait-LogContains {
    param([string]$Pattern, [int]$TimeoutMs = 30000, [string]$What = '')
    if (-not $What) { $What = "日志出现 [$Pattern]" }
    return Wait-For -TimeoutMs $TimeoutMs -What $What -Probe {
        $txt = Get-RecentLogText
        if ($txt -match $Pattern) { $txt } else { $null }
    }
}

function Assert-Log {
    param([string]$Pattern, [string]$Name, [int]$TimeoutMs = 30000)
    $found = $false
    try {
        Wait-LogContains $Pattern $TimeoutMs | Out-Null
        $found = $true
    } catch {
        $found = $false
    }
    Assert-True $found $Name "(未匹配: $Pattern)"
}

# ---------- 连接状态 ----------

function Ensure-Connected {
    param([int]$TimeoutMs = 90000, [bool]$ClearLogFirst = $true)
    $root = $script:AppRoot
    if (-not $root) { throw 'AppRoot 未初始化，请先 Start-App' }

    if ($ClearLogFirst) { Clear-Log }

    $s = Get-WindowStateText $root
    if ($s -like '已连接 *') { return $s }

    Set-Edit (Find-Edit $root 'ServerBox') '127.0.0.1'
    Set-Edit (Find-Edit $root 'PortBox') '25566'
    Set-Edit (Find-Edit $root 'UserBox') $script:TestPlayer
    Set-Edit (Find-Edit $root 'VersionBox') 'auto'
    Invoke-UiButton '连接'

    $st = Wait-For -TimeoutMs $TimeoutMs -What '连接成功' -Probe {
        $s2 = Get-WindowStateText $root
        if ($s2 -like '已连接 *') { $s2 }
    }
    # 真正进入世界之后才允许发 RCON 布置命令，
    # 否则 execute as <玩家> 匹配不到实体，命令会被服务端静默忽略。
    Wait-LogContains '已进入游戏' 30000 | Out-Null
    return $st
}

function Disconnect-App {
    param([int]$TimeoutMs = 60000)
    $st = Get-WindowStateText $script:AppRoot
    if ($st -like '已连接 *') {
        Invoke-UiButton '断开'
    } else {
        Write-Host "  [ui] skip disconnect (state=$st)"
    }
    Wait-For -TimeoutMs $TimeoutMs -What '断开完成' -Probe {
        $s = Get-WindowStateText $script:AppRoot
        if ($s -eq '未连接') { $s }
    } | Out-Null
}

function Send-MccCommand {
    param([string]$Text)
    $box = Find-Edit $script:AppRoot 'CommandBox'
    Set-Edit $box $Text
    Invoke-UiButton '发送'
    Start-Sleep -Milliseconds 600
}

# ---------- 世界/玩家状态（全部走 RCON） ----------

function Get-PlayerHealth {
    # 死亡玩家 Health=0；不在线则拿不到数据
    $r = Invoke-RconPlayer 'data get entity @s Health'
    if ($r -match '([0-9]+(?:\.[0-9]+)?)') { return [double]$Matches[1] }
    return -1.0
}

function Ensure-PlayerAlive {
    $st = Get-WindowStateText $script:AppRoot
    if ($st -notlike '已连接 *') {
        Ensure-Connected | Out-Null
    }

    $h = Get-PlayerHealth
    if ($h -gt 0) { return $h }

    # 玩家带着死亡数据（Health=0）进服：用 MCC 内部命令 /respawn 复活
    Write-Host "  player dead (health=$h) -> sending /respawn"
    Send-MccCommand '/respawn'
    Start-Sleep -Seconds 2
    $h2 = Get-PlayerHealth
    if ($h2 -gt 0) {
        Assert-True $true 'player revived via /respawn' "(health=$h2)"
        return $h2
    }

    Write-Host "  /respawn did not work (health=$h2) -> full reconnect"
    Clear-Log
    Disconnect-App
    Ensure-Connected | Out-Null
    $h3 = Get-PlayerHealth
    Assert-True ($h3 -gt 0) 'player alive after reconnect' "(health=$h3)"
    return $h3
}

function Set-PlayerGamemode {
    param([string]$Mode = 'creative')
    $out = Invoke-Rcon ("gamemode {0} {1}" -f $Mode, $script:TestPlayer)
    Write-Host "  rcon 'gamemode $Mode' -> $out"
    # gamemode 的 RCON 回包偶尔为空，用 playerGameType 回读做权威校验
    # 0=生存 1=创造 2=冒险 3=旁观
    $code = @{ survival = 0; creative = 1; adventure = 2; spectator = 3 }[$Mode]
    $gt = Invoke-RconPlayer 'data get entity @s playerGameType'
    Write-Host "  playerGameType -> $gt"
    return ($gt -match (": ?{0}\s*$" -f $code))
}

function Get-PlayerPos {
    # 返回 @($x,$y,$z)（double），失败返回 $null
    $raw = Invoke-RconPlayer 'data get entity @s Pos'
    if ($raw -match '\[(-?[\d.]+)d,\s*(-?[\d.]+)d,\s*(-?[\d.]+)d\]') {
        return @([double]$Matches[1], [double]$Matches[2], [double]$Matches[3])
    }
    Write-Host "  ! 解析 Pos 失败: $raw"
    return $null
}

function Summon-ZombieNear {
    # 注意：execute as <玩家> run summon ~ ~ ~ 的 ~ 是 RCON 控制台位置（世界出生点），
    # 不是玩家位置，客户端收不到生成包。必须算出绝对坐标。
    param([double]$Dist = 2.0)
    $pos = Get-PlayerPos
    if (-not $pos) { return $false }
    $out = Invoke-Rcon (
        "summon minecraft:zombie {0} {1} {2}" -f ($pos[0] + $Dist), [math]::Floor($pos[1]), $pos[2])
    Write-Host "  rcon summon -> $out"
    return ($out -match 'Summoned')
}

function Set-ServerDifficulty {
    param([string]$Level = 'peaceful')
    $out = Invoke-Rcon ("difficulty {0}" -f $Level)
    Write-Host "  rcon 'difficulty $Level' -> $out"
    return $true
}

function Setup-World {
    # 让玩家进入可安全测试的状态：活着 + 创造 + 和平（清掉残留怪物）
    param([bool]$NeedMobs = $false, [bool]$Night = $false)

    $null = Ensure-PlayerAlive
    $gm = Set-PlayerGamemode 'creative'
    Assert-True $gm 'set creative mode (rcon output checked)'

    $null = Set-ServerDifficulty 'peaceful'
    if ($Night) { $null = Invoke-Rcon 'time set night' }
    if ($NeedMobs) {
        $null = Set-ServerDifficulty 'easy'
        $null = Invoke-Rcon 'time set night'
    }
    Start-Sleep -Milliseconds 800
}

function Set-TestArena {
    # 高空开阔平台：四周是空气，避免“召唤的怪物卡进方块窒息”“脚下没立足点”等干扰。
    # 用绝对坐标（execute as 的 ~ 是 RCON 控制台位置，绝不能用）。
    $x = -345; $y = 110; $z = -443

    foreach ($dx in 0..3) {
        foreach ($dz in -1..1) {
            $null = Invoke-Rcon ("setblock {0} {1} {2} stone" -f ($x + $dx), ($y - 1), ($z + $dz))
        }
    }

    $out = Invoke-Rcon ("execute as MccXBot run tp @s {0} {1} {2} facing {3} {4} {5}" -f `
        $x, $y, $z, ($x + 1.0), ($y + 1.62), $z)
    Write-Host "  arena tp -> [$out]"
    Start-Sleep -Milliseconds 900

    $pos = Get-PlayerPos
    if ($pos) {
        Write-Host ("  arena pos -> ({0}, {1}, {2})" -f $pos[0], $pos[1], $pos[2])
        if ([math]::Abs($pos[1] - $y) -gt 1) {
            Write-Host "  ! 场地传送到位失败，重试一次"
            $out = Invoke-Rcon ("execute as MccXBot run tp @s {0} {1} {2}" -f $x, $y, $z)
            Write-Host "  arena tp retry -> [$out]"
            Start-Sleep -Milliseconds 900
        }
    }
    return $true
}

# ---------- RCON ----------

function Invoke-Rcon {
    param([string]$Command, [int]$TimeoutMs = 5000)

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect($script:RconHost, $script:RconPort)
    } catch {
        $client.Close()
        throw "RCON 连接失败: $_"
    }

    $stream = $client.GetStream()
    $stream.ReadTimeout = $TimeoutMs
    $enc = [System.Text.Encoding]::UTF8

    function Write-RconPacket([int]$id, [int]$type, [string]$body) {
        $b = $enc.GetBytes($body)
        $len = 4 + 4 + $b.Length + 2
        $ms = New-Object System.IO.MemoryStream
        $bw = New-Object System.IO.BinaryWriter($ms)
        $bw.Write([int]$len)
        $bw.Write([int]$id)
        $bw.Write([int]$type)
        $bw.Write($b)
        $bw.Write([byte]0)
        $bw.Write([byte]0)
        $bw.Flush()
        $buf = $ms.ToArray()
        $stream.Write($buf, 0, $buf.Length)
        $stream.Flush()
        $bw.Dispose()
        $ms.Dispose()
    }

    function Read-RconPacket {
        $head = New-Object byte[] 4
        $read = 0
        while ($read -lt 4) {
            $n = $stream.Read($head, $read, 4 - $read)
            if ($n -le 0) { throw 'RCON 连接被关闭' }
            $read += $n
        }
        $len = [BitConverter]::ToInt32($head, 0)
        if ($len -lt 10 -or $len -gt 4110) { throw "RCON 包长度异常: $len" }
        $payload = New-Object byte[] $len
        $read = 0
        while ($read -lt $len) {
            $n = $stream.Read($payload, $read, $len - $read)
            if ($n -le 0) { throw 'RCON 数据包被截断' }
            $read += $n
        }
        $rid = [BitConverter]::ToInt32($payload, 0)
        $rtype = [BitConverter]::ToInt32($payload, 4)
        $bodyLen = $len - 10
        $body = ''
        if ($bodyLen -gt 0) { $body = $enc.GetString($payload, 8, $bodyLen) }
        return @{ Id = $rid; Type = $rtype; Body = $body }
    }

    try {
        Write-RconPacket 1 3 $script:RconPass
        $auth = Read-RconPacket
        if ($auth.Id -eq -1) { throw 'RCON 认证失败' }

        Write-RconPacket 2 2 $Command
        $text = ''
        $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)
        while ([DateTime]::UtcNow -lt $deadline) {
            try {
                $r = Read-RconPacket
            } catch {
                break
            }
            if ($r.Id -eq 2) {
                $text += $r.Body
            }
            if (-not $stream.DataAvailable) {
                # 服务端可能先回一个空包再回正文，静默窗口要够长
                Start-Sleep -Milliseconds 350
                if (-not $stream.DataAvailable) { break }
            }
        }
        return $text
    } finally {
        try { $stream.Dispose() } catch { }
        $client.Close()
    }
}

function Invoke-RconPlayer {
    param([string]$Command, [int]$TimeoutMs = 5000)
    return Invoke-Rcon -Command ("execute as {0} run {1}" -f $script:TestPlayer, $Command) -TimeoutMs $TimeoutMs
}
