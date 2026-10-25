# 服务器登录密码的两项新需求（2026-10-23 用户要求）回归：
#   D = 密码框自带“按住显示”改成点眼睛常驻显示；密码框下方两个勾选框
#       （左「记住密码」、右「自动登录」；只有勾了记住密码，自动登录才可选）。
#   E = 再弹密码框时自动填入记住的密码，等 1 秒自动点登录（不用手点）。
#   F = 重启程序后“记住密码 / 自动登录 / 密码”都还在（随加密账号库落盘）。
#   G = 强杀 MCCX 主进程后，账号子进程必须一起消失，服务器立刻看到玩家离线
#       （用户报的“关掉窗口后账号还挂在服务器上”）。
# 对话框由本地测试服的 mccx_dialog 数据包提供（mccx:test_login，含 password 输入项）。
# 全程专用测试账号 TestLogin，结束删除，不碰用户已有账号；只用 UIA + RCON，不抢前台焦点。
# 用法：powershell -ExecutionPolicy Bypass -File tests\cases\g_loginopt.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:TestUser = 'TestLogin'
$script:TestPort = '25599'
# autolib 默认测试玩家是 MccXBot；本脚本账号叫 TestLogin，RCON 目标要跟着改
$script:TestPlayer = $script:TestUser
$script:TestPassword = 'MccX-Pass-2026'

$script:ServerDir = "$(Join-Path $PSScriptRoot '..\artifacts')\mcserver\26.1"
$script:DialogPackDir = Join-Path $script:ServerDir 'world\datapacks\mccx_dialog'

function Find-EditByIdSafe {
    param([string]$Id)
    try { return Find-ById $script:AppRoot $Id } catch { return $null }
}

function Find-CheckBoxByName {
    param([string]$Name)
    $c = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::CheckBox)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)))
    return $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

function Set-CheckState {
    # UIA 的 ToggleState 会滞后：拨完要轮询到目标状态（其它用例踩过）
    param($El, [bool]$On)
    if (-not $El) { return $false }
    $want = if ($On) { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }
    for ($i = 0; $i -lt 20; $i++) {
        try {
            $tp = $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
            if ($tp.Current.ToggleState -eq $want) { return $true }
            $tp.Toggle()
        } catch { }
        Start-Sleep -Milliseconds 150
    }
    return $false
}

function Get-CheckState {
    param($El)
    if (-not $El) { return $null }
    try {
        $tp = $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        return $tp.Current.ToggleState
    } catch { return $null }
}

function Add-TestAccount {
    Invoke-UiButton '添加账号'
    $null = Wait-For -TimeoutMs 10000 -What '添加账号弹窗出现' -Probe {
        if (Find-EditByIdSafe 'NewServerBox') { 'open' } else { $null }
    }

    $cb = $null
    for ($i = 0; $i -lt 10 -and -not $cb; $i++) {
        $cb = Find-CheckBoxByName '立即连接'
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

# ---------- 本地测试服 + 对话框数据包（与 g_dialog 同一套夹具）----------
function Test-LocalServerListening {
    return [bool](netstat -ano | Select-String ":$($script:TestPort)\s.*LISTENING")
}

function Write-DialogDatapack {
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
    try {
        $out = Invoke-Rcon 'dialog show @a mccx:test_login' 4000
    } catch {
        return $false
    }
    return ($out -notmatch "Can't find element")
}

function Start-TestServerProcess {
    $java = Get-TestServerJava
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

# ---------- 服务器侧 ----------
function Show-TestDialog {
    Invoke-Rcon ('dialog show {0} mccx:test_login' -f $script:TestPlayer) | Out-Null
}

function Test-PlayerOnline {
    try {
        $out = Invoke-Rcon 'list' 5000
    } catch {
        return $null
    }
    return [bool]($out -match ('\b' + [regex]::Escape($script:TestPlayer) + '\b'))
}

# ---------- UIA：密码框 / 眼睛按钮 / 弹窗按钮 ----------
function Get-PasswordBoxEl {
    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Edit)
    $edits = $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($e in $edits) {
        try { if ($e.Current.IsPassword) { return $e } } catch { }
    }
    return $null
}

function Test-PasswordBoxVisible {
    return ($null -ne (Get-PasswordBoxEl))
}

function Find-ButtonByName {
    param([string]$Name)
    $cond = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Button)),
        (New-Object System.Windows.Automation.PropertyCondition($script:AE::NameProperty, $Name)))
    return $script:AppRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Find-DialogButtonNear {
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

function Set-PasswordValue {
    # 密码框读回来的值是一串圆点，不能像普通输入框那样写完再校验（Set-Edit 会判失败），
    # 这里只调 ValuePattern.SetValue，后面的断言靠“明文切换后能看到原文”来验。
    param($El, [string]$Text)
    if (-not $El) { throw '密码框不存在，无法写入' }
    $vp = $El.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $vp.SetValue($Text)
}

function Get-ButtonNamesDump {    $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Button)
    $btns = $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $names = @()
    foreach ($b in $btns) { try { $names += ('[' + $b.Current.Name + ']') } catch { } }
    return ($names -join ' ')
}

function Assert-NotInLog {
    param([string]$Pattern, [string]$Name, [int]$WaitMs = 1200)
    Start-Sleep -Milliseconds $WaitMs
    $txt = ''
    try { $txt = Get-RecentLogText -Max 160 } catch { }
    Assert-True (-not $txt.Contains($Pattern)) $Name "(出现: $Pattern)"
}

function Wait-DialogOpen {
    param([int]$TimeoutMs = 15000)
    $pw = $null
    try {
        $pw = Wait-For -TimeoutMs $TimeoutMs -What '密码框出现' -Probe {
            $el = Get-PasswordBoxEl
            if ($el) { $el } else { $null }
        }
    } catch { $pw = $null }
    return $pw
}

function Invoke-LoginAction {
    param($Pw)
    $login = Find-DialogButtonNear 'login' $Pw
    if (-not $login) {
        try {
            $login = Wait-For -TimeoutMs 5000 -What 'login 按钮出现' -Probe {
                $b = Find-DialogButtonNear 'login' (Get-PasswordBoxEl)
                if ($b) { $b } else { $null }
            }
        } catch { $login = $null }
    }
    if (-not $login) {
        Write-Host "  [dbg] 未找到 login 按钮，当前按钮: $(Get-ButtonNamesDump)"
        throw '找不到对话框的 login 动作按钮'
    }
    Invoke-Element $login
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
    Assert-True ($now -eq ($before + 1)) 'D0 测试账号已添加' "count=$now before=$before"

    Select-TestAccount
    Invoke-UiButton '连接'
    Assert-Connected 'D0b 本地测试服连接成功' 90000
    Assert-Log '已进入游戏' 'D0c 进入游戏' 60000
    Start-Sleep -Seconds 2

    # ============ D. 密码框 / 两个勾选框 ============
    Write-Host "`n=== D. 密码框：点眼睛常驻显示 + 记住密码 / 自动登录 ==="
    Clear-Log
    Show-TestDialog
    $pw = Wait-DialogOpen 15000
    Assert-True ($null -ne $pw) 'D1 服务器对话框弹出且渲染成密码框（IsPassword）'
    if (-not $pw) { throw '密码框没出来，后续断言无法进行' }

    $remember = $null
    $auto = $null
    try {
        $remember = Wait-For -TimeoutMs 8000 -What '记住密码勾选框出现' -Probe {
            $c = Find-CheckBoxByName '记住密码'
            if ($c) { $c } else { $null }
        }
    } catch { $remember = $null }
    Assert-True ($null -ne $remember) 'D2 密码框下方有「记住密码」勾选框'

    $auto = Find-CheckBoxByName '自动登录'
    Assert-True ($null -ne $auto) 'D3 密码框下方有「自动登录」勾选框'

    $autoEnabled = $false
    if ($auto) { try { $autoEnabled = $auto.Current.IsEnabled } catch { } }
    Assert-True (-not $autoEnabled) 'D4 没勾记住密码时「自动登录」是禁用的' "enabled=$autoEnabled"

    $ok = Set-CheckState $remember $true
    Assert-True $ok 'D5 勾上「记住密码」'
    $autoEnabled = $false
    for ($i = 0; $i -lt 20; $i++) {
        if ($auto) { try { $autoEnabled = $auto.Current.IsEnabled } catch { } }
        if ($autoEnabled) { break }
        Start-Sleep -Milliseconds 150
    }
    Assert-True $autoEnabled 'D6 勾了记住密码后「自动登录」变为可选'

    # 眼睛：点一下常驻显示，再点一下回到隐藏
    $eye = Find-ButtonByName '显示密码'
    if (-not $eye) { $eye = Find-ButtonByName '隐藏密码' }
    Assert-True ($null -ne $eye) 'D7 密码框右侧有显示 / 隐藏密码的切换按钮'

    Set-PasswordValue $pw $script:TestPassword
    Start-Sleep -Milliseconds 400

    $plainVisible = $false
    if ($eye) {
        $null = Set-CheckState $eye $true
        for ($i = 0; $i -lt 20; $i++) {
            $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Edit)
            $edits = $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
            foreach ($e in $edits) {
                try {
                    if ($e.Current.IsPassword) { continue }
                    $val = $e.Current.Value
                    if ([string]::IsNullOrEmpty($val)) {
                        try {
                            $tp = $e.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
                            $val = $tp.DocumentRange.GetText(-1)
                        } catch { }
                    }
                    if (($val -as [string]).Trim() -eq $script:TestPassword) { $plainVisible = $true }
                } catch { }
            }
            if ($plainVisible) { break }
            Start-Sleep -Milliseconds 150
        }
        if (-not $plainVisible) {
            try { Write-Host ("  [dbg] log: " + (Get-RecentLogText -Max 12)) } catch { }
            $cond = New-Object System.Windows.Automation.PropertyCondition($script:AE::ControlTypeProperty, $script:CT::Edit)
            $edits = $script:AppRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
            foreach ($e in $edits) {
                try {
                    $val = $e.Current.Value
                    if ([string]::IsNullOrEmpty($val)) {
                        try {
                            $tp = $e.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern)
                            $val = $tp.DocumentRange.GetText(-1)
                        } catch { }
                    }
                    Write-Host ("  [dbg] edit: pw=" + $e.Current.IsPassword + " offscreen=" + $e.Current.IsOffscreen + " value=[" + $val + "]")
                } catch {
                    Write-Host "  [dbg] edit: (取值失败)"
                }
            }
        }
    }
    Assert-True $plainVisible 'D8 点一下眼睛：密码明文显示（常驻，不是按住）'

    $hiddenAgain = $false
    if ($eye) {
        $null = Set-CheckState $eye $false
        for ($i = 0; $i -lt 20; $i++) {
            if (Test-PasswordBoxVisible) { $hiddenAgain = $true; break }
            Start-Sleep -Milliseconds 150
        }
    }
    Assert-True $hiddenAgain 'D9 再点一下：回到密码遮蔽状态'

    $null = Set-CheckState $auto $true
    $autoOn = Get-CheckState $auto
    Assert-True ($autoOn -eq [System.Windows.Automation.ToggleState]::On) 'D10 勾上「自动登录」'

    $pw = Get-PasswordBoxEl
    Invoke-LoginAction $pw
    Assert-Log '已提交服务器对话框' 'D11 点 login 提交成功' 10000

    $closed = $false
    for ($i = 0; $i -lt 40; $i++) {
        if (-not (Test-PasswordBoxVisible)) { $closed = $true; break }
        Start-Sleep -Milliseconds 150
    }
    Assert-True $closed 'D12 提交后弹窗关闭'
    Assert-NotInLog $script:TestPassword 'D13 密码没有进日志' 800

    Start-Sleep -Seconds 3

    # ============ E. 再弹密码框：自动填入并 1 秒后自动登录 ============
    Write-Host "`n=== E. 再弹密码框：自动登录（不用手点） ==="
    Clear-Log
    Show-TestDialog
    Assert-Log '自动登录' 'E1 打出自动登录日志' 20000
    Assert-Log '已提交服务器对话框' 'E2 自动提交了登录动作' 15000

    $closed2 = $false
    for ($i = 0; $i -lt 60; $i++) {
        if (-not (Test-PasswordBoxVisible)) { $closed2 = $true; break }
        Start-Sleep -Milliseconds 150
    }
    Assert-True $closed2 'E3 弹窗自动关闭（没有人工点按钮）'

    # ============ F. 重启程序：设置与密码仍随账号保留 ============
    Write-Host "`n=== F. 重启程序后仍然记住（随加密账号库落盘） ==="
    Stop-App
    Start-Sleep -Seconds 2
    $null = Restart-App
    Start-Sleep -Seconds 3
    Select-TestAccount
    Invoke-UiButton '连接'
    Assert-Connected 'F0 重启后再连接成功' 90000
    Assert-Log '已进入游戏' 'F0b 进入游戏' 60000
    Start-Sleep -Seconds 2

    Clear-Log
    Show-TestDialog
    Assert-Log '自动登录' 'F1 重启后弹密码框仍然自动登录' 20000
    Assert-Log '已提交服务器对话框' 'F2 重启后自动提交成功' 15000
    Start-Sleep -Seconds 2

    # ============ G. 强杀主进程：子进程必须一起消失、服务器看到离线 ============
    Write-Host "`n=== G. 强杀 MCCX 主进程：子进程一起退出 + 服务器看到离线 ==="
    $onlineBefore = Test-PlayerOnline
    Assert-True ($onlineBefore -eq $true) 'G1 前置：玩家在线' "online=$onlineBefore"

    $mainPid = Get-MainProcId
    Assert-True ($mainPid -gt 0) 'G2 取到主进程 PID' "pid=$mainPid"
    Write-Host "  [kill] Stop-Process -Id $mainPid -Force"
    Stop-Process -Id $mainPid -Force -ErrorAction SilentlyContinue

    $runnerGone = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 500
        $runners = @(Get-CimInstance Win32_Process -Filter "Name='MCCX.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -match '--runner' })
        if ($runners.Count -eq 0) { $runnerGone = $true; break }
    }
    Assert-True $runnerGone 'G3 子进程随主进程一起退出（没有孤儿 runner）'

    $offline = $false
    for ($i = 0; $i -lt 40; $i++) {
        $on = Test-PlayerOnline
        if ($on -eq $false) { $offline = $true; break }
        Start-Sleep -Milliseconds 500
    }
    Assert-True $offline 'G4 服务器立刻看到玩家离线（不再挂着在线）'
} finally {
    Write-Host "`n=== 收尾：断开 + 删除测试账号 ==="
    $null = Restart-App
    Start-Sleep -Seconds 2
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
Write-Host "SUMMARY g_loginopt FAILURES=$fail"
exit ([int]($fail -gt 0))
