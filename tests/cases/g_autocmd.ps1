# 自动指令 e2e（2026-10-22 需求，第二轮补充）：
#   卡片「自动指令」= 总开关 + 参数弹层；弹层里可以新建任意多条指令，
#   每条自己的「间隔（秒）」「进服立即执行」「执行方式（循环/只执行一次）」「循环次数」「日志」相互独立。
# 覆盖：
#   A 新建账号 / 连接 / 弹层里新建两行并填参数（第 1 行勾进服立即执行，间隔 2 秒；
#     第 2 行不勾，间隔 6 秒）；
#   B 打开总开关 → 断线重连：第 1 行「进服立即执行」立刻发出、第 2 行要等 6 秒才发；
#   C 定时循环：第 1 行每 2 秒一条，7 秒窗口里至少两条；
#   D 每条指令自己的日志开关：两行都关掉后不再产生「[自动指令]」过程日志，但指令照常发出
#     （服务器回显仍在）；只把第 1 行打开，日志只恢复第 1 行的；
#   E 落盘：重启程序后两条指令、间隔、进服立即执行、每条日志开关都还在；
#   F 执行方式与次数 + MCC 内部命令：/send（MCC 内部命令，不是服务器指令）能执行；
#     只执行一次的那条只发一遍；次数=2 的那条发两遍后停（日志里有「已执行完设置次数」）。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tests\cases\g_autocmd.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:TestUser = 'TestAutoCmd'
$script:TestPort = '25599'
$script:TestPlayer = $script:TestUser

$script:Token1 = 'acc_one'
$script:Token2 = 'acc_two'
$script:Token3 = 'acc_three'
$script:Token4 = 'acc_four'

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

# ---------- 自动指令弹层 ----------

function Show-AutoCmdPanel {
    # 「自动指令」在功能区最右边：先横向滚到底让按钮进入可视区，再展开弹层。
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
    # 弹层是 light dismiss 的：留着它会挡住卡片的开关，先收起。
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
    # 弹层里同名控件是"每行一个"，只能按树顺序取全部（FindAll 顺序 = 行顺序）。
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
function Get-RowJoinToggles { return (Get-NamedElements '自动指令进服立即执行' $script:CT::Button) }
function Get-RowLogToggles { return (Get-NamedElements '自动指令日志' $script:CT::Button) }
function Get-RowModes { return (Get-NamedElements '自动指令方式' $script:CT::ComboBox) }
function Get-RowCounts { return (Get-NamedElements '自动指令次数' $script:CT::Edit) }

function Set-ToggleEl {
    # 按元素拨开关（弹层里同名控件每行一个，Set-FeatureToggle 只能拿第一个）。
    # UIA 的 ToggleState 不是同步更新的：拨完要轮询到目标状态再返回，否则断言会拿到旧值。
    param($El, [bool]$On)
    $tp = $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if (($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) -ne $On) {
        $tp.Toggle()
    }
    for ($i = 0; $i -lt 20; $i++) {
        Start-Sleep -Milliseconds 150
        if (($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) -eq $On) { return $true }
    }
    return $false
}

function Set-RowMode {
    # 执行方式下拉（每行一个同名 ComboBox）：展开 → 选列表项 → 收起。
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

function Get-RowModeText {
    param($Combo)
    try {
        $sel = $Combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
        if ($sel.Count -gt 0) { return $sel[0].Current.Name }
    } catch { }
    return ''
}

function Get-EchoCount {
    # 只数"服务器回显"那几行：过程日志 [自动指令] ... 和服务器回显里都有 token，
    # 混在一起数会把"执行一次"数成 2 次，这里把过程日志行剔掉再数。
    param([string]$Token)
    $n = 0
    foreach ($line in ((Get-RecentLogText) -split "`r?`n")) {
        if ($line -match [regex]::Escape($Token) -and $line -notmatch '\[自动指令\]') { $n++ }
    }
    return $n
}

function Set-RowValue {
    param($Element, [string]$Value)
    Set-Edit $Element $Value
    Start-Sleep -Milliseconds 150
}

function Assert-LogAbsent {
    # 负向断言：等一段时间后，最近一屏日志里不能出现这个标记（用于"此刻还没发/被挡住"）。
    param([string]$Token, [string]$Name, [int]$WaitMs = 2500)
    Start-Sleep -Milliseconds $WaitMs
    $txt = Get-RecentLogText
    if ($txt -match [regex]::Escape($Token)) {
        $lines = @($txt -split "`r?`n")
        Write-Host ("  [dbg] 最近日志: " + (($lines | Select-Object -Last 20) -join ' | '))
        Assert-True $false $Name "不该出现在日志里: $Token"
        return
    }
    Assert-True $true $Name "已确认不存在: $Token"
}

function Wait-RowCount {
    # 弹层里加/删一行后 ItemsControl 会重建容器，UIA 树有短暂空窗：别用固定 sleep，轮询到位为止。
    param([int]$Expect, [int]$TimeoutMs = 10000)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if ((Get-RowTexts).Count -eq $Expect) { return $Expect }
        Start-Sleep -Milliseconds 250
    }
    return (Get-RowTexts).Count
}

function Get-LogHitCount {
    param([string]$Token)
    $txt = Get-RecentLogText
    return ([regex]::Matches($txt, [regex]::Escape($Token))).Count
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

# ============================================================
Clear-StrayRunners
$appRoot = Restart-App
Start-Sleep -Seconds 3
if (-not $script:AppRoot) { $script:AppRoot = $appRoot }
$before = Get-AccountCount
Write-Host "--- app up (accounts=$before) ---"

try {
    # ============ A. 起服 + 建测试账号 + 连接 + 新建两条指令 ============
    Write-Host "`n=== A. 建账号 / 连接 / 新建两条指令 ==="
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

    # 弹层里新建第 1 条：进服立即执行 + 2 秒
    Show-AutoCmdPanel
    Invoke-UiButton '新建自动指令'
    $rowsNow = Wait-RowCount -Expect 1
    $texts = Get-RowTexts
    $intervals = Get-RowIntervals
    Assert-True ($rowsNow -eq 1) 'A4 新建后弹层里出现一行' "rows=$rowsNow"
    Set-RowValue $texts[0] "/me $($script:Token1)"
    $intervals = Get-RowIntervals
    Set-RowValue $intervals[0] '2'

    $toggles = Get-RowJoinToggles
    $tp = $toggles[0].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($tp.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) {
        $tp.Toggle()
        Start-Sleep -Milliseconds 300
    }
    Assert-True ($tp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) 'A5 第 1 行勾上了「进服立即执行」'

    # 第 2 条：不勾进服立即执行，间隔 6 秒
    Invoke-UiButton '新建自动指令'
    $rowsNow = Wait-RowCount -Expect 2
    $texts = Get-RowTexts
    $intervals = Get-RowIntervals
    Assert-True ($rowsNow -eq 2) 'A6 再新建后有两行' "rows=$rowsNow"
    Set-RowValue $texts[1] "/me $($script:Token2)"
    $intervals = Get-RowIntervals
    Set-RowValue $intervals[1] '6'

    $toggles = Get-RowJoinToggles
    $tp2 = $toggles[1].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    Assert-True ($tp2.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) 'A7 第 2 行默认不勾「进服立即执行」'
    Hide-AutoCmdPanel

    # 总开关：打开
    Set-FeatureToggle '自动指令' $true | Out-Null
    Assert-Log '自动指令已开启' 'A8 自动指令总开关已打开' 15000

    # 先连上、再开开关（不重连）：MCC 的 AfterGameJoined 已经过去了，Bot 必须自己补一次进服初始化。
    Assert-Log "TestAutoCmd $($script:Token1)" 'A9 连上之后再开开关也立刻生效（不用重连）' 25000

    # ============ B. 断线重连：进服立即执行 vs 到点才执行 ============
    Write-Host "`n=== B. 重连验证「进服立即执行」与独立间隔 ==="
    Disconnect-App
    Clear-Log
    Invoke-UiButton '连接'
    Assert-Connected 'B1 重连成功' 90000
    Assert-Log '已进入游戏' 'B2 重新进入游戏' 60000

    Assert-Log "TestAutoCmd $($script:Token1)" 'B3 勾了进服立即执行的那条已由服务器回显（立刻执行）' 25000
    Assert-LogAbsent "TestAutoCmd $($script:Token2)" 'B4 没勾的那条（6 秒）此时还没发' 1500
    Assert-Log "TestAutoCmd $($script:Token2)" 'B5 6 秒后没勾的那条也按自己的间隔发出' 25000

    # ============ C. 定时循环 ============
    Write-Host "`n=== C. 定时循环（2 秒那条） ==="
    Clear-Log
    Start-Sleep -Seconds 7
    $hits = Get-LogHitCount "TestAutoCmd $($script:Token1)"
    Assert-True ($hits -ge 2) 'C1 7 秒窗口里 2 秒那条至少执行两次' "hits=$hits"

    # ============ D. 每条指令自己的日志开关 ============
    Write-Host "`n=== D. 每条指令自己的日志开关 ==="
    Show-AutoCmdPanel
    $logToggles = Get-RowLogToggles
    Assert-True ($logToggles.Count -eq 2) 'D0 每条指令各有一个自己的日志开关' "count=$($logToggles.Count)"
    Assert-True (Set-ToggleEl $logToggles[0] $false) 'D0b 第 1 行的日志开关已关'
    $logToggles = Get-RowLogToggles
    Assert-True (Set-ToggleEl $logToggles[1] $false) 'D0c 第 2 行的日志开关已关'
    Hide-AutoCmdPanel
    Clear-Log
    Start-Sleep -Seconds 6
    $txt = Get-RecentLogText
    Assert-True ($txt -notmatch '\[自动指令\]') 'D1 两条都把日志关掉后不再产生「[自动指令]」过程日志'
    Assert-True ($txt -match [regex]::Escape("TestAutoCmd $($script:Token1)")) 'D2 日志关了指令照常执行（服务器回显仍在）'

    Show-AutoCmdPanel
    $logToggles = Get-RowLogToggles
    Assert-True (Set-ToggleEl $logToggles[0] $true) 'D3a 只把第 1 行的日志开关重新打开'
    Hide-AutoCmdPanel
    Clear-Log
    Assert-Log '[自动指令]' 'D3 只把第 1 行的日志开关打开，过程日志就恢复' 20000

    # ============ E. 落盘：重启后还在 ============
    Write-Host "`n=== E. 重启程序验证落盘 ==="
    Start-Sleep -Seconds 4
    $appRoot = Restart-App
    Start-Sleep -Seconds 3
    if (-not $script:AppRoot) { $script:AppRoot = $appRoot }
    Select-TestAccount

    Show-AutoCmdPanel
    $rowsNow = Wait-RowCount -Expect 2
    $texts = Get-RowTexts
    $intervals = Get-RowIntervals
    $toggles = Get-RowJoinToggles
    Assert-True ($rowsNow -eq 2) 'E1 重启后两条指令还在' "rows=$rowsNow"

    $row1 = Get-EditValue $texts[0]
    $row2 = Get-EditValue $texts[1]
    $int1 = Get-EditValue $intervals[0]
    $int2 = Get-EditValue $intervals[1]
    Assert-True ($row1 -eq "/me $($script:Token1)") 'E2 第 1 行指令文本已落盘' "text=$row1"
    Assert-True ($int1 -eq '2') 'E3 第 1 行间隔已落盘' "interval=$int1"
    Assert-True ($row2 -eq "/me $($script:Token2)") 'E4 第 2 行指令文本已落盘' "text=$row2"
    Assert-True ($int2 -eq '6') 'E5 第 2 行间隔已落盘' "interval=$int2"

    $tg1 = $toggles[0].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $tg2 = $toggles[1].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    Assert-True ($tg1.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) 'E6 第 1 行「进服立即执行」已落盘'
    Assert-True ($tg2.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) 'E7 第 2 行「进服立即执行」保持不勾'

    $logToggles = Get-RowLogToggles
    $lg1 = $logToggles[0].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $lg2 = $logToggles[1].GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    Assert-True ($lg1.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) 'E8 第 1 行的日志开关（开）已落盘'
    Assert-True ($lg2.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::Off) 'E9 第 2 行的日志开关（关）已落盘'
    Hide-AutoCmdPanel

    $mt = Get-UiToggle '自动指令'
    $mtp = $mt.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    Assert-True ($mtp.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) 'E10 总开关状态已落盘'

    # ============ F. 执行方式 / 循环次数 / MCC 内部命令 ============
    Write-Host "`n=== F. 执行方式（只执行一次 / 循环次数）与 MCC 内部命令 ==="
    Invoke-UiButton '连接'
    Assert-Connected 'F1 重新连接成功' 90000
    Assert-Log '已进入游戏' 'F2 重新进入游戏' 60000

    Show-AutoCmdPanel
    Invoke-UiButton '新建自动指令'
    $rowsNow = Wait-RowCount -Expect 3
    $texts = Get-RowTexts
    Assert-True ($rowsNow -eq 3) 'F3 再新建一行' "rows=$rowsNow"

    # 第 3 行：MCC 内部命令 /send（不是服务器指令），只执行一次，间隔 4 秒
    Set-RowValue $texts[2] "/send TestAutoCmd $($script:Token3)"
    Set-RowValue (Get-RowIntervals)[2] '4'
    $modes = Get-RowModes
    Assert-True ($modes.Count -eq 3) 'F4 每行都有「执行方式」下拉' "count=$($modes.Count)"
    Assert-True ((Get-RowModeText $modes[2]) -eq '循环执行') 'F5 新行默认「循环执行」' "mode=$(Get-RowModeText $modes[2])"

    $countsBefore = (Get-RowCounts).Count
    Assert-True ($countsBefore -eq 3) 'F6 循环执行的行都有「次数」输入框（默认空 = 无限）' "count=$countsBefore"

    Set-RowMode (Get-RowModes)[2] '只执行一次'
    Assert-True ((Get-RowModeText (Get-RowModes)[2]) -eq '只执行一次') 'F7 第 3 行改成「只执行一次」'
    Start-Sleep -Milliseconds 500
    $countsNow = (Get-RowCounts).Count
    Assert-True ($countsNow -eq 2) 'F8 「只执行一次」的行不再显示「次数」输入框' "count=$countsNow"

    # 第 4 行：循环 + 次数 2，间隔 4 秒。
    # 文本用 MCC 内部命令 /send（不是 /me）：/me 的服务器回显在日志里会出现两行，
    # 数"执行了几次"会翻倍；/send 一次只留一行回显。
    Invoke-UiButton '新建自动指令'
    $rowsNow = Wait-RowCount -Expect 4
    $texts = Get-RowTexts
    Assert-True ($rowsNow -eq 4) 'F9 再新建一行（共四条）' "rows=$rowsNow"
    Set-RowValue $texts[3] "/send TestAutoCmd $($script:Token4)"
    Set-RowValue (Get-RowIntervals)[3] '4'
    # 第 3 行是「只执行一次」，它没有「次数」框：按位置取会错位，取最后一个（= 第 4 行）
    $counts = Get-RowCounts
    Assert-True ($counts.Count -eq 3) 'F9b 目前有三个「次数」框（第 3 行没有）' "count=$($counts.Count)"
    Set-RowValue $counts[$counts.Count - 1] '2'
    Hide-AutoCmdPanel

    Clear-Log
    Start-Sleep -Seconds 16
    $txt = Get-RecentLogText
    Assert-True ($txt -match '（MCC 命令）') 'F10 /send 走的是 MCC 内部命令这条路（日志里有「（MCC 命令）」）'
    Assert-True ($txt -notmatch '不完整的命令') 'F11 /send 没被当成服务器指令发出去（服务器没报未知命令）'
    $onceHits = Get-EchoCount "TestAutoCmd $($script:Token3)"
    Assert-True ($onceHits -eq 1) 'F12 「只执行一次」的行 16 秒里只执行了一次' "hits=$onceHits"
    $twoHits = Get-EchoCount "TestAutoCmd $($script:Token4)"
    Assert-True ($twoHits -eq 2) 'F13 次数=2 的行执行两次后停止' "hits=$twoHits"
    Assert-True ($txt -match '已执行完设置次数') 'F14 次数用完后日志提示已停止'
} finally {
    Write-Host "`n=== 收尾：断开 + 删除测试账号 ==="
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
    if ($after -ne $before) {
        $after = Wait-AccountCount -Expect $before -TimeoutMs 10000
    }
    Assert-True ($after -eq $before) 'X1 账号数已恢复（未污染用户账号库）' "after=$after before=$before"
}

Write-Host "`n=== 收尾：关闭程序 ==="
Stop-App
$fail = Get-FailureCount
Write-Host "SUMMARY g_autocmd FAILURES=$fail"
exit ([int]($fail -gt 0))
