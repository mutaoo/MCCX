# 取消密码框后无限重连的回归（2026-10-04 用户实测 bug + 现场抓取定位）：
#   服务器弹出登录对话框时，用户取消后的那次断开不能自动重连，否则服务器踢人会触发
#   “取消 → 断开 → 自动重连 → 又弹框”的死循环（现场每 ~30 秒一轮、计数永远是第 1 次）。
#   取消有两条通路，都要压住：
#     B = MCCX 弹窗自带的「取消」关窗按钮（走 CancelDialog）；
#     C = 服务器动作里的 cancel 按钮（走 SubmitDialog，按 IsCancel 认取消）。
#   A = 负向对照：没取消过时，踢下线必须照常自动重连（证明压制不是无脑生效）。
# 对话框由本地测试服的 mccx_dialog 数据包提供（mccx:test_login，含 password 输入项，
# 会渲染成密码框）；测试自己写入数据包，注册表只在服务器启动时读，所以会按需重启测试服。
# 服务器对取消的回应用 RCON /kick 模拟（原 bug 即服务器踢人断开）。
# 全程专用测试账号 TestDialog，结束删除，不碰用户已有账号；只用 UIA + RCON，不抢前台焦点。
# 用法：pwsh -ExecutionPolicy Bypass -File tests\cases\g_dialog.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:TestUser = 'TestDialog'
$script:TestPort = '25599'
# autolib 默认测试玩家是 MccXBot；本脚本账号叫 TestDialog，RCON 目标要跟着改
$script:TestPlayer = $script:TestUser

$script:ServerDir = "$(Join-Path $PSScriptRoot '..\artifacts')\mcserver\1.21.11"
$script:DialogPackDir = Join-Path $script:ServerDir 'world\datapacks\mccx_dialog'

# ---------- 账号库操作（与 p_multi / g_refill / g_bow 一致） ----------
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

# ---------- 本地测试服 + 对话框数据包 ----------
function Test-LocalServerListening {
    return [bool](netstat -ano | Select-String ":$($script:TestPort)\s.*LISTENING")
}

function Write-DialogDatapack {
    # 夹具自愈：世界目录被清掉时把数据包写回来。
    # JSON 必须无 BOM（原版按 UTF-8 严格解析）；改了对话框内容要手动重启测试服，
    # 因为 minecraft:dialog 注册表只在服务器启动时读一次（/reload 不够，实测过）。
    $mcmeta = @'
{
    "pack": {
        "pack_format": 94,
        "min_format": 94,
        "max_format": 94,
        "description": "MccX dialog cancel test"
    }
}
'@
    $dialog = @'
{
    "type": "minecraft:multi_action",
    "title": "MccX dialog test",
    "can_close_with_escape": true,
    "after_action": "close",
    "columns": 1,
    "inputs": [
        {
            "type": "minecraft:text",
            "key": "password",
            "label": "password"
        }
    ],
    "actions": [
        {
            "label": "login",
            "width": 200,
            "action": {
                "type": "minecraft:custom",
                "id": "mccx:test_login"
            }
        },
        {
            "label": "cancel",
            "width": 200,
            "action": {
                "type": "minecraft:custom",
                "id": "mccx:test_cancel"
            }
        }
    ]
}
'@
    $dir = Join-Path $script:DialogPackDir 'data\mccx\dialog'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null

    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    $pairs = @(
        @( (Join-Path $script:DialogPackDir 'pack.mcmeta'), $mcmeta ),
        @( (Join-Path $dir 'test_login.json'), $dialog )
    )
    foreach ($p in $pairs) {
        $old = ''
        try { if (Test-Path -LiteralPath $p[0]) { $old = [System.IO.File]::ReadAllText($p[0]) } } catch { }
        if ($old -cne $p[1]) {
            [System.IO.File]::WriteAllText($p[0], $p[1], $utf8NoBom)
            Write-Host ("  [srv] 写入数据包文件: " + $p[0])
        }
    }
}

function Test-DialogRegistered {
    # 注册表里有 mccx:test_login → 返回 $true（RCON 输出 “Can't find element” = 没注册）
    try {
        $out = Invoke-Rcon 'dialog show @a mccx:test_login' 4000
    } catch {
        return $false
    }
    return ($out -notmatch "Can't find element")
}

function Start-TestServerProcess {
    $java = (Get-Command java -ErrorAction SilentlyContinue).Source
    if (-not $java) { throw 'java 不在 PATH，无法启动本地测试服' }
    Start-Process -FilePath $java -ArgumentList '-Xmx1G', '-jar', 'server.jar', 'nogui' `
        -WorkingDirectory $script:ServerDir -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $script:ServerDir 'test.out.log') `
        -RedirectStandardError (Join-Path $script:ServerDir 'test.err.log')
    for ($i = 0; $i -lt 90; $i++) {
        Start-Sleep -Seconds 2
        if (Test-LocalServerListening) { return }
    }
    throw "本地测试服启动超时（$($script:TestPort) 未监听）"
}

function Stop-TestServerProcess {
    try { Invoke-Rcon 'stop' 8000 | Out-Null } catch { }
    for ($i = 0; $i -lt 60; $i++) {
        if (-not (Test-LocalServerListening)) { Start-Sleep -Seconds 1; return }
        Start-Sleep -Seconds 1
    }
    throw '本地测试服未能停止'
}

function Ensure-TestServer {
    if (-not (Test-Path (Join-Path $script:ServerDir 'server.jar'))) { throw "本地测试服缺失: $script:ServerDir" }
    Write-DialogDatapack
    if (Test-LocalServerListening) {
        if (Test-DialogRegistered) {
            Write-Host "  [srv] 本地测试服已在运行 127.0.0.1:$($script:TestPort)，对话框已注册"
            return
        }
        Write-Host '  [srv] 服务器在跑但对话框未注册，重启以加载数据包'
        Stop-TestServerProcess
    } else {
        Write-Host "  [srv] 启动本地测试服 127.0.0.1:$($script:TestPort)"
    }
    Start-TestServerProcess
    if (-not (Test-DialogRegistered)) { throw '对话框 mccx:test_login 未注册（数据包加载失败，看服务器日志）' }
    Write-Host '  [srv] 对话框 mccx:test_login 已注册'
}

function Clear-StrayRunners {
    $strays = @(Get-CimInstance Win32_Process -Filter "Name='MCCX.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '--runner' })
    if ($strays.Count -eq 0) { return }
    Write-Host ("  [clean] 清理残留 runner: " + (($strays | ForEach-Object { $_.ProcessId }) -join ','))
    Get-Process -Name MCCX -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
    Start-Sleep -Seconds 2
}

# ---------- 服务器侧：弹对话框 / 踢人 ----------
function Show-TestDialog {
    Invoke-Rcon ('dialog show {0} mccx:test_login' -f $script:TestPlayer) | Out-Null
}

function Kick-TestPlayer {
    # 模拟服务器对“取消/超时”的回应：把玩家踢下线
    Invoke-Rcon ('kick {0}' -f $script:TestPlayer) | Out-Null
}

# ---------- UIA：密码框 / 弹窗按钮 ----------
function Test-PasswordBoxVisible {
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Edit)
    $edits = $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($e in $edits) {
        try { if ($e.Current.IsPassword) { return $true } } catch { }
    }
    return $false
}

function Find-DialogButtonNear {
    # ContentDialog 里的按钮没有 AutomationId，按文案全树找可能撞到别处的同名按钮，
    # 所以要求它和密码框同屏（中心距 <= 600px）才算数。
    param([string]$Name, $NearEl)
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Button)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)))
    $btns = $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($b in $btns) {
        try {
            if (-not $b.Current.IsEnabled) { continue }
            $r1 = $b.Current.BoundingRectangle
            $r2 = $NearEl.Current.BoundingRectangle
            if ($r1.IsEmpty -or $r2.IsEmpty) { continue }
            $dx = [math]::Abs(($r1.X + $r1.Width / 2) - ($r2.X + $r2.Width / 2))
            $dy = [math]::Abs(($r1.Y + $r1.Height / 2) - ($r2.Y + $r2.Height / 2))
            if ($dx -le 600 -and $dy -le 600) { return $b }
        } catch { }
    }
    return $null
}

function Get-PasswordBoxEl {
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Edit)
    $edits = $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($e in $edits) {
        try { if ($e.Current.IsPassword) { return $e } } catch { }
    }
    return $null
}

function Get-ButtonNamesDump {
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Button)
    $btns = $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $names = @()
    foreach ($b in $btns) { try { $names += ('[' + $b.Current.Name + ']') } catch { } }
    return ($names -join ' ')
}

function Assert-NotInLog {
    param([string]$Pattern, [string]$Name, [int]$WaitMs = 12000)
    Start-Sleep -Milliseconds $WaitMs
    $txt = ''
    try { $txt = Get-RecentLogText -Max 120 } catch { }
    Assert-True (-not $txt.Contains($Pattern)) $Name "(出现: $Pattern)"
}

# ============================================================
Clear-StrayRunners
$appRoot = Restart-App
Start-Sleep -Seconds 3
if (-not $script:AppRoot) { $script:AppRoot = $appRoot }
$before = Get-AccountCount
Write-Host "--- app up (accounts=$before) ---"

try {
    # ============ 准备：起服（带对话框数据包）+ 建账号 + 连接 ============
    Ensure-TestServer
    Remove-ResidueAccount
    $before = Get-AccountCount

    Add-TestAccount
    Assert-True ((Get-AccountCount) -eq ($before + 1)) 'A0 测试账号已添加' "count=$(Get-AccountCount) before=$before"

    Select-TestAccount
    Set-ToggleOn '自动重连' $true
    Assert-True (Get-ToggleOn '自动重连') 'A0b 自动重连开关已打开（A 组对照的前提）'

    Clear-Log
    Invoke-UiButton '连接'
    Assert-Log '连接成功' 'A0c 本地测试服连接成功' 90000
    Assert-Log '已进入游戏' 'A0d 进入游戏' 60000
    Start-Sleep -Seconds 2

    # ============ A. 负向对照：没取消过 → 踢下线照常自动重连 ============
    Write-Host "`n=== A. 未取消时踢下线：必须照常自动重连 ==="
    Clear-Log
    Kick-TestPlayer
    Assert-Log '秒后自动重连' 'A1 未取消时踢下线触发自动重连' 60000
    Assert-Log '连接成功' 'A2 自动重连成功' 120000
    Assert-Log '已进入游戏' 'A3 重连后进入游戏' 60000
    Start-Sleep -Seconds 2

    # ============ B. 关窗取消（MCCX 自带「取消」按钮）→ 踢下线不再重连 ============
    Write-Host "`n=== B. 点 MCCX 的「取消」关窗：踢下线不再自动重连 ==="
    Clear-Log
    Show-TestDialog
    $pw = $null
    try {
        $pw = Wait-For -TimeoutMs 15000 -What '密码框出现' -Probe {
            $el = Get-PasswordBoxEl
            if ($el) { $el } else { $null }
        }
    } catch { $pw = $null }
    Assert-True ($null -ne $pw) 'B1 服务器对话框弹出且渲染成密码框'

    $closeBtn = $null
    if ($pw) {
        $closeBtn = Find-DialogButtonNear '取消' $pw
        if (-not $closeBtn) {
            Write-Host "  [dbg] 未找到「取消」按钮，当前按钮: $(Get-ButtonNamesDump)"
            try {
                $closeBtn = Wait-For -TimeoutMs 8000 -What '「取消」按钮出现' -Probe {
                    $b = Find-DialogButtonNear '取消' (Get-PasswordBoxEl)
                    if ($b) { $b } else { $null }
                }
            } catch { $closeBtn = $null }
        }
    }
    Assert-True ($null -ne $closeBtn) 'B2 找到弹窗自带的「取消」按钮'
    if ($closeBtn) { Invoke-Element $closeBtn }

    $closed = $null
    try {
        $closed = Wait-For -TimeoutMs 8000 -What '对话框关闭' -Probe {
            if (-not (Test-PasswordBoxVisible)) { 'closed' } else { $null }
        }
    } catch { $closed = $null }
    Assert-True ($null -ne $closed) 'B3 点「取消」后弹窗关闭'
    Assert-NotInLog '已提交服务器对话框' 'B3b 走的是关窗取消通路（不是提交动作）' 800

    Kick-TestPlayer
    Assert-Log '这次断开不再自动重连' 'B4 取消后踢下线：打出“不再自动重连”日志' 30000
    Assert-NotInLog '秒后自动重连' 'B5 取消后踢下线：确实没有再发起自动重连' 12000
    $st = Get-WindowStateText $script:AppRoot
    Assert-True ($st -eq '未连接') 'B6 状态停在「未连接」' "state=$st"

    # ============ C. 服务器动作里的 cancel 按钮 → 同样压制 ============
    Write-Host "`n=== C. 点服务器动作 cancel：踢下线不再自动重连 ==="
    Clear-Log
    Invoke-UiButton '连接'
    Assert-Log '连接成功' 'C0 手动重新连接' 90000
    Assert-Log '已进入游戏' 'C0b 重新进入游戏' 60000
    Start-Sleep -Seconds 2

    Clear-Log
    Show-TestDialog
    $pw2 = $null
    try {
        $pw2 = Wait-For -TimeoutMs 15000 -What '密码框再次出现' -Probe {
            $el = Get-PasswordBoxEl
            if ($el) { $el } else { $null }
        }
    } catch { $pw2 = $null }
    Assert-True ($null -ne $pw2) 'C1 对话框再次弹出'

    $cancelBtn = $null
    if ($pw2) {
        $cancelBtn = Find-DialogButtonNear 'cancel' $pw2
        if (-not $cancelBtn) {
            Write-Host "  [dbg] 未找到 cancel 按钮，当前按钮: $(Get-ButtonNamesDump)"
            try {
                $cancelBtn = Wait-For -TimeoutMs 8000 -What 'cancel 按钮出现' -Probe {
                    $b = Find-DialogButtonNear 'cancel' (Get-PasswordBoxEl)
                    if ($b) { $b } else { $null }
                }
            } catch { $cancelBtn = $null }
        }
    }
    Assert-True ($null -ne $cancelBtn) 'C2 找到服务器动作按钮 cancel'
    if ($cancelBtn) { Invoke-Element $cancelBtn }

    Assert-Log '已提交服务器对话框' 'C3 点 cancel 走的是提交动作通路' 8000
    $closed2 = $null
    try {
        $closed2 = Wait-For -TimeoutMs 8000 -What '对话框关闭' -Probe {
            if (-not (Test-PasswordBoxVisible)) { 'closed' } else { $null }
        }
    } catch { $closed2 = $null }
    Assert-True ($null -ne $closed2) 'C4 点 cancel 后弹窗关闭'

    Kick-TestPlayer
    Assert-Log '这次断开不再自动重连' 'C5 动作 cancel 后踢下线：打出“不再自动重连”日志' 30000
    Assert-NotInLog '秒后自动重连' 'C5b 动作 cancel 后踢下线：确实没有再发起自动重连' 12000
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
Write-Host "SUMMARY g_dialog FAILURES=$fail"
exit ([int]($fail -gt 0))
