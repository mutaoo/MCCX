# 服务器信息过滤 e2e（2026-10-04 需求：全屏蔽 / 只屏蔽玩家消息 / 只显示指定前缀 / 只屏蔽指定前缀）：
#   三批起过滤方式与前缀框收进了行尾"服务器信息过滤"下拉按钮的弹层（无总开关）：
#   每个用例节在 Clear-Log 后先 Show-FilterPanel 展开弹层，再操作里面的下拉框 / 前缀框。
#   连本地测试服 → 下拉切过滤模式 → RCON tellraw 造服务器消息 → 核对日志看得见 / 看不见：
#   A（默认不过滤）：玩家格式（<名字> 开头）与系统消息都显示；
#   B（全屏蔽）：两类消息都不显示；内部命令 /help 的输出（Info 频道）照常显示——
#     证明只拦"服务器聊天"一个频道，连接提示 / Bot 日志不误伤；
#   C（只屏蔽玩家消息）：`<名字>` 开头被挡、系统消息放行；
#   D（只显示指定前缀）：`[mfpre]` 开头放行，无前缀的系统 / 玩家消息都挡；
#     D5/D6 多前缀列表（逗号分隔，二批新增）：命中列表内任一前缀都放行，列表外的仍挡；
#   F（只屏蔽指定前缀，二批新增）：`[mfblk]` 开头被挡，其余放行；F5 验证多前缀列表；
#   E（恢复不过滤）：消息重新可见，前缀框内容保留。
# 全程专用测试账号 TestMsgFilter，结束删除，不碰用户已有账号；只用 UIA + RCON，不抢前台焦点。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File tests\cases\g_msgfilter.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:TestUser = 'TestMsgFilter'
$script:TestPort = '25599'
# autolib 默认测试玩家是 MccXBot，本脚本账号叫 TestMsgFilter；不改的话
# RCON 的 tellraw / execute as 全部落空（与 g_walk / p_filter / g_view 同款处理）。
$script:TestPlayer = $script:TestUser

# ---------- 账号库操作（与 p_multi / p_filter / g_view / g_bow / g_walk 一致） ----------
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

# ---------- 消息注入与断言 ----------
function Send-Tellraw {
    # 造一条"服务器发来的消息"（区别于玩家自己发的聊天）；
    # 带 <tester> 的是玩家格式，不带的是系统消息。
    param([string]$Text)
    Invoke-Rcon ("tellraw {0} {1}" -f $script:TestPlayer, ('{"text":"' + $Text + '"}')) 4000 | Out-Null
    Start-Sleep -Milliseconds 500
}

function Assert-LogAbsent {
    # 负向断言：给消息一点落地时间后，最近日志里不能出现这个标记；
    # 命中就把最近日志打出来，方便一眼看到是哪种消息漏了。
    param([string]$Token, [string]$Name, [int]$WaitMs = 2500)
    Start-Sleep -Milliseconds $WaitMs
    $txt = Get-RecentLogText
    if ($txt -match [regex]::Escape($Token)) {
        $lines = @($txt -split "`r?`n")
        Write-Host ("  [dbg] 最近日志: " + (($lines | Select-Object -Last 20) -join ' | '))
        Assert-True $false $Name "不该出现在日志里: $Token"
        return
    }
    Assert-True $true $Name "已屏蔽: $Token"
}

function Show-FilterPanel {
    # 过滤方式和前缀框都收进了行尾"服务器信息过滤"下拉按钮的弹层（2026-10-04 三批）。
    # 弹层关闭时里面的控件不在 UIA 树里：先把功能区横向滚到最右（按钮在行尾），
    # 再展开弹层；之后 Set-ComboItem / Set-UiValue 才找得到控件。已展开则不重复操作。
    # 注意：弹层会因外部点击（清空日志等）收起，所以每个用例节都在 Clear-Log 之后再调它。
    $el = $null
    try { $el = Get-UiButton '服务器信息过滤选项' } catch { }
    if (-not $el) { return }
    try {
        $w = [System.Windows.Automation.TreeWalker]::RawViewWalker
        $node = $el
        for ($i = 0; $i -lt 10 -and $null -ne $node; $i++) {
            try {
                $sp = $node.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
                if ($sp.Current.HorizontalViewSize -lt 100 -or $sp.Current.HorizontalScrollPercent -ge 0) {
                    $sp.SetScrollPercent(100, -1)   # 横向滚到最右（-1 = 纵向不动）
                    Start-Sleep -Milliseconds 300
                    break
                }
            } catch { }
            $node = $w.GetParent($node)
        }
    } catch { }

    $btn = Get-UiButton '服务器信息过滤选项'
    $ec = $btn.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($ec.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
        $ec.Expand()
        Start-Sleep -Milliseconds 600
    }
}

# ============================================================
Clear-StrayRunners
$appRoot = Restart-App
Start-Sleep -Seconds 3
if (-not $script:AppRoot) { $script:AppRoot = $appRoot }
$before = Get-AccountCount
Write-Host "--- app up (accounts=$before) ---"

try {
    # ============ A. 起服 + 建测试账号 + 连接（默认不过滤） ============
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

    Clear-Log
    Send-Tellraw '<tester> mfbase_p1'
    Assert-Log 'mfbase_p1' 'A4 默认不过滤：玩家格式消息可见' 15000
    Send-Tellraw 'mfbase_s1'
    Assert-Log 'mfbase_s1' 'A5 默认不过滤：系统消息可见' 15000

    # ============ B. 全屏蔽：服务器消息全部不显示 ============
    Write-Host "`n=== B. 全屏蔽 ==="
    Clear-Log
    Show-FilterPanel
    Set-ComboItem '服务器信息过滤' '全屏蔽'
    Assert-Log '服务器信息过滤：全屏蔽' 'B1 切到全屏蔽并有模式提示' 15000

    Send-Tellraw '<tester> mfblock_p1'
    Assert-LogAbsent 'mfblock_p1' 'B2 全屏蔽：玩家格式消息不显示'

    Send-Tellraw 'mfblock_s1'
    Assert-LogAbsent 'mfblock_s1' 'B3 全屏蔽：系统消息不显示'

    # 只拦 Chat 频道：/help 输出走 Info 频道，必须照常显示（连接提示 / Bot 日志同理）
    # /help 正文上百行，日志只留最近 60 条，行首的 "Available commands" 会滚出窗口；
    # 断言改匹配输出的固定尾行 "For server help…"（它永远是最后一条）。
    Send-MccCommand '/help'
    $b4ok = $false
    try {
        Wait-LogContains 'For server help' 15000 | Out-Null
        $b4ok = $true
    } catch {
        $b4ok = $false
    }
    if (-not $b4ok) {
        $b4txt = Get-RecentLogText
        $b4lines = @($b4txt -split "`r?`n")
        Write-Host ('  [dbg] B4 最近日志: ' + (($b4lines | Select-Object -Last 25) -join ' | '))
    }
    Assert-True $b4ok 'B4 全屏蔽下非聊天日志（内部命令输出）照常显示' '(未匹配: For server help)'

    # ============ C. 只屏蔽玩家消息：挡 <名字>，放系统消息 ============
    Write-Host "`n=== C. 只屏蔽玩家消息 ==="
    Clear-Log
    Show-FilterPanel
    Set-ComboItem '服务器信息过滤' '屏蔽玩家消息'
    Assert-Log '服务器信息过滤：只屏蔽玩家消息' 'C1 切到只屏蔽玩家消息' 15000

    Send-Tellraw '<tester> mfcp_p1'
    Assert-LogAbsent 'mfcp_p1' 'C2 玩家格式消息被挡'

    Send-Tellraw 'mfcp_s1'
    Assert-Log 'mfcp_s1' 'C3 系统消息放行' 15000

    # ============ D. 只显示指定前缀：[mfpre] 放行，其余全挡 ============
    Write-Host "`n=== D. 只显示指定前缀 ==="
    Clear-Log
    Show-FilterPanel
    # 2026-10-05：两个前缀框改成按方式互斥显示（选"只显示"才出上面那个），
    # 所以必须先切方式、再填"只显示"框。提示行里会带上刚填的前缀。
    Set-ComboItem '服务器信息过滤' '只显示指定前缀'
    Set-UiValue '服务器信息只显示前缀' '[mfpre]'
    Assert-Log '服务器信息过滤：只显示以' 'D1 切到只显示指定前缀（提示带前缀）' 15000

    Send-Tellraw '[mfpre] mfdp_s1'
    Assert-Log 'mfdp_s1' 'D2 带前缀的系统消息放行' 15000

    Send-Tellraw 'mfdp_no1'
    Assert-LogAbsent 'mfdp_no1' 'D3 无前缀的系统消息被挡'

    Send-Tellraw '<tester> mfdp_p1'
    Assert-LogAbsent 'mfdp_p1' 'D4 无前缀的玩家消息被挡'

    # ---- D5/D6 多前缀列表（2026-10-04 二批：逗号分隔，命中任一项即放行） ----
    Set-UiValue '服务器信息只显示前缀' '[mfpre],[mfpre2]'
    Send-Tellraw '[mfpre2] mflist_s2'
    Assert-Log 'mflist_s2' 'D5 列表里的第二个前缀也放行' 15000

    Send-Tellraw '[mfpre3] mflist_no2'
    Assert-LogAbsent 'mflist_no2' 'D6 列表外的前缀仍被挡'

    # ============ F. 只屏蔽指定前缀：[mfblk] 挡，其余放行 ============
    Write-Host "`n=== F. 只屏蔽指定前缀 ==="
    Clear-Log
    Show-FilterPanel
    # 同上：先切到"只屏蔽"，"只屏蔽"框才会出现，再填它
    Set-ComboItem '服务器信息过滤' '只屏蔽指定前缀'
    Set-UiValue '服务器信息只屏蔽前缀' '[mfblk]'
    Assert-Log '服务器信息过滤：只屏蔽以' 'F1 切到只屏蔽指定前缀' 15000

    Send-Tellraw '[mfblk] mfbk_s1'
    Assert-LogAbsent 'mfbk_s1' 'F2 带屏蔽前缀的系统消息被挡'

    Send-Tellraw 'mfbk_ok1'
    Assert-Log 'mfbk_ok1' 'F3 不带屏蔽前缀的系统消息放行' 15000

    Send-Tellraw '<tester> mfbk_p1'
    Assert-Log 'mfbk_p1' 'F4 不带屏蔽前缀的玩家消息放行' 15000

    # 多前缀列表：命中列表里任一前缀都挡
    Set-UiValue '服务器信息只屏蔽前缀' '[mfblk],[mfblk2]'
    Send-Tellraw '[mfblk2] mfbk_s2'
    Assert-LogAbsent 'mfbk_s2' 'F5 列表里的第二个前缀也挡'

    # ============ E. 恢复不过滤 ============
    Write-Host "`n=== E. 恢复不过滤 ==="
    Clear-Log
    Show-FilterPanel
    Set-ComboItem '服务器信息过滤' '不过滤'
    Assert-Log '服务器信息过滤：不过滤' 'E1 恢复不过滤' 15000

    Send-Tellraw '<tester> mfend_p1'
    Assert-Log 'mfend_p1' 'E2 恢复后消息重新可见' 15000

    # ============ E4（2026-10-05 新增）两份前缀各自独立生效 + 框按方式互斥显示 ============
    # 两个前缀框现在是互斥显示的（选"只显示"只出上面那个），所以不能再靠"读另一个框的值"来验，
    # 改成行为验证。先切回"只屏蔽"（E 节末尾已改成不过滤），验"只显示"框里的前缀不生效。
    Show-FilterPanel
    Set-ComboItem '服务器信息过滤' '只屏蔽指定前缀'
    Send-Tellraw '[mfpre] mfbk_iso1'
    Assert-Log 'mfbk_iso1' 'E4a 切到"只屏蔽"后，"只显示"框里的前缀不生效（各自独立）' 15000

    Send-Tellraw '[mfblk] mfbk_iso2'
    Assert-LogAbsent 'mfbk_iso2' 'E4b "只屏蔽"框的前缀仍然生效'

    # 再切回"只显示"：此时"只屏蔽"框填的 [mfblk] 不该把 [mfpre] 的消息挡掉
    Set-ComboItem '服务器信息过滤' '只显示指定前缀'
    Send-Tellraw '[mfpre] mfbk_iso3'
    Assert-Log 'mfbk_iso3' 'E4c 切回"只显示"后，"只屏蔽"框的前缀不生效（两份互不干扰）' 15000

    Set-ComboItem '服务器信息过滤' '不过滤'
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
    Assert-True ($after -eq $before) 'X1 账号数已恢复（未污染用户账号库）' "after=$after before=$before"
}

Write-Host "`n=== 收尾：关闭程序 ==="
Stop-App
$fail = Get-FailureCount
Write-Host "SUMMARY g_msgfilter FAILURES=$fail"
exit ([int]($fail -gt 0))
