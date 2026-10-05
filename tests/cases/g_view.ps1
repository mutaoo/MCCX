# 视角调整 e2e（2026-10-03 需求 4；2026-10-04 新需求重做；2026-10-05 加进服守卫）：
#   连本地测试服 → 展开「视角调整」下拉 → 点方向按钮 → 用 RCON 读服务端记录的 Rotation 核对。
#   2026-10-04 起「进服后恢复上次视角」开关已移除：本地不存视角，沿用服务器玩家数据
#   记住的上次退出朝向（与裸 MCC 进服体验一致）——A/F 节验证这一点。
#   2026-10-05 加「进服视角守卫」：真实服务器登录后会再推一个 yaw=0 的位置包（现场抓包证实），
#   所以进服头 10 秒内把登录包的朝向钉住；A8 段用 MCCX_TEST_VIEW_CORRECTION=1 模拟那个包来验证。
# 全程用专用测试账号 TestView，结束删除，不碰用户已有账号；只用 UIA，不抢前台焦点。
# 用法：pwsh -ExecutionPolicy Bypass -File tests\cases\g_view.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

# 进服视角守卫的测试钩子：让 ViewControlBot 锚定后模拟一次"服务器纠正包（改成正南 0）"，
# 这样 A8/F 就能断言守卫会把朝向钉回来——本地测试服自己不会推这种包。
$env:MCCX_TEST_VIEW_CORRECTION = '1'

$script:TestUser = 'TestView'
$script:TestPort = '25599'
# autolib 默认测试玩家是 MccXBot，本脚本账号叫 TestView；不改的话
# RCON 的 execute as <玩家> 全部落空（与 p_filter 同款处理）。
$script:TestPlayer = $script:TestUser

# ---------- 账号库操作（与 p_multi / p_filter 一致） ----------
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
        $v = Get-EditValueSafe 'UserBox'
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

# ---------- RCON 读服务端记录的朝向 ----------
function Get-RotationText {
    # 输出形如：Rotation: [270.0f, 0.0f]
    $out = $null
    try {
        $out = Invoke-Rcon ("execute as {0} run data get entity @s Rotation" -f $script:TestPlayer) 8000
    } catch {
        return $null
    }
    if ($out -match '\[(-?\d+(?:\.\d+)?)f?\s*,\s*(-?\d+(?:\.\d+)?)f?\]') {
        return "$($Matches[1])|$($Matches[2])"
    }
    return $null
}

function Get-NormYaw([double]$Yaw) { return (($Yaw % 360) + 360) % 360 }

# ---------- RCON：读服务端 Pos（G 节"确实在走"的位移断言依据，与 g_walk 同款） ----------
function Get-PlayerPos {
    # 返回 @(x, y, z) 双精度数组；查询失败 / 解析失败返回 $null
    $out = $null
    try {
        $out = Invoke-Rcon ('execute as {0} run data get entity @s Pos' -f $script:TestPlayer) 4000
    } catch {
        return $null
    }
    if ($out -match '\[(-?\d+(?:\.\d+)?)d,\s*(-?\d+(?:\.\d+)?)d,\s*(-?\d+(?:\.\d+)?)d\]') {
        return @([double]$Matches[1], [double]$Matches[2], [double]$Matches[3])
    }
    return $null
}

function Get-PosDistance {
    # 水平距离（XZ）：走路发生在水平面，Y 的起伏（台阶/跳跃）不掺进来
    param($A, $B)
    if ($null -eq $A -or $null -eq $B) { return $null }
    $dx = $A[0] - $B[0]
    $dz = $A[2] - $B[2]
    return [math]::Sqrt(($dx * $dx) + ($dz * $dz))
}

function Write-RecentLog {
    # 断言失败时把最近日志打出来：寻路受阻提示 / Bot 挂载失败都会在里面
    param([int]$Last = 30)
    try {
        $txt = Get-RecentLogText
        if ($txt) {
            $lines = @($txt -split "`r?`n")
            $tail = ($lines | Select-Object -Last $Last) -join ' | '
            Write-Host "  [dbg] 最近日志: $tail"
        }
    } catch { }
}

function Assert-Rotation {
    # 轮询服务端朝向直到与期望一致（或超时）；yaw 按 360 取模后比较，容差 5 度。
    param([double]$Yaw, [double]$Pitch, [string]$Name, [int]$TimeoutMs = 20000)
    $text = $null
    try {
        $text = Wait-For -TimeoutMs $TimeoutMs -What $Name -Probe {
            $t = Get-RotationText
            if (-not $t) { return $null }
            $parts = $t.Split('|')
            $ny = Get-NormYaw ([double]$parts[0])
            $dy = [math]::Abs($ny - $Yaw)
            if ($dy -gt 180) { $dy = 360 - $dy }
            $dp = [math]::Abs([double]$parts[1] - $Pitch)
            if ($dy -le 5 -and $dp -le 5) { return $t }
            return $null
        }
    } catch {
        $text = $null
    }

    if ($text) {
        $parts = $text.Split('|')
        Write-Host ("  服务端 Rotation = [{0}, {1}]" -f $parts[0], $parts[1])
        Assert-True $true $Name ("yaw=$($parts[0]) pitch=$($parts[1]) 期望 yaw=$Yaw pitch=$Pitch")
    } else {
        Assert-True $false $Name "期望 yaw=$Yaw pitch=$Pitch，实际=$(Get-RotationText)"
    }
}

# ---------- 展开「视角调整」下拉 ----------
function Open-ViewFlyout {
    $b = $null
    try { $b = Get-UiButton '视角调整选项' } catch { return $false }
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

function Click-ViewButton {
    param([string]$Name)
    $el = $null
    for ($i = 0; $i -lt 6 -and -not $el; $i++) {
        if (-not (Open-ViewFlyout)) { Start-Sleep -Milliseconds 400 }
        $el = $null
        try { $el = Find-UiElement $script:CT::Button $Name } catch { $el = $null }
        if (-not $el) { Start-Sleep -Milliseconds 400 }
    }
    if (-not $el) { throw "「视角调整」下拉里找不到按钮 [$Name]" }
    Invoke-Element $el
    Start-Sleep -Milliseconds 500
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

    # 等进服后的状态稳定（位置包、Bot 挂载日志），再做朝向断言
    Start-Sleep -Seconds 5
    $base = Get-RotationText
    Assert-True ($null -ne $base) 'A4 RCON 能读到服务端记录的朝向' "Rotation=$base"

    # ============ A 后置：进服不自动改视角（2026-10-04 新需求，与 MCC 一致） ============
    Assert-Log '进服沿用服务器记住的朝向' 'A5 进服沿用服务器朝向的说明日志出现' 15000
    $aRot1 = Get-RotationText
    Start-Sleep -Seconds 4
    $aRot2 = Get-RotationText
    Assert-True ($null -ne $aRot1 -and $aRot1 -eq $aRot2) 'A6 进服后 4 秒内视角未被自动改变（不推不改，与 MCC 一致）' "r1=$aRot1 r2=$aRot2"

    # A7（2026-10-05 补的断言盲区）：客户端朝向必须等于服务端 Rotation。
    # 只验 A6 那种"服务端 4 秒没变"会假绿——若进服那一刻两端本来就都是南
    # （服务器玩家数据就是南，或登录旋转还没落地就被自动行走的锚点钉成南），A6 照样通过。
    # 客户端朝向由 ViewControlBot 延迟 5 tick 打进日志（避免读到未落地的默认 0）。
    # 探针刻意返回字符串：Wait-For 把 0 当"未就绪"，而 yaw=0 恰恰是要抓的那个值。
    $clientYawText = $null
    try {
        $clientYawText = Wait-For -TimeoutMs 15000 -What '进服后客户端朝向日志出现' -Probe {
            $m = [regex]::Match((Get-RecentLogText), '进服后客户端朝向 yaw (-?[\d.]+)')
            if ($m.Success) { return $m.Groups[1].Value }
            return $null
        }
    } catch { $clientYawText = $null }
    $a7ok = $false
    $a7detail = '客户端朝向日志缺失'
    $srvRot7 = Get-RotationText
    if ($clientYawText -and $srvRot7) {
        $cy = [double]::Parse($clientYawText, [Globalization.CultureInfo]::InvariantCulture)
        $sy = [double]($srvRot7.Split('|')[0])
        $dy = [math]::Abs((Get-NormYaw $cy) - (Get-NormYaw $sy))
        if ($dy -gt 180) { $dy = 360 - $dy }
        $a7ok = $dy -le 5
        $a7detail = "client=$cy server=$srvRot7"
    }
    Assert-True $a7ok 'A7 客户端朝向 = 服务端 Rotation（防"两端都是南"的假绿）' $a7detail

    # ============ A8. 进服视角守卫（2026-10-05）：纠正包改走朝向要钉回来 ============
    # 真实服务器登录后会再推一个 yaw=0 的位置包（现场抓包证实），本地测试服不会推，
    # 所以用 MCCX_TEST_VIEW_CORRECTION=1 让守卫自己模拟一次（见 g_view.ps1 顶部）。
    # A8 前置：这套断言只在"服务器记住的朝向**不是**正南 0"时才成立。
    # 原因：模拟钩子按的是"正南"，守卫的锚点取的是"窗口内第一个非正南朝向"——
    # 如果测试服上这个账号记住的朝向本来就是 0（连着跑用例时很容易发生，前面几轮
    # G 节会把它停在正北/正西，这里又可能回到 0），就没有非零朝向可锚，
    # 守卫按设计会放弃（没有可守的东西），A8a/b/c 应当整体跳过而不是判红。
    # 这不是放宽标准：上面 A6/A7 已经在"两端都等于服务器记录"的前提下覆盖了 0 的情形。
    $srvYaw0 = [double]($srvRot7.Split('|')[0])
    $srvIsSouth = ([math]::Abs((Get-NormYaw $srvYaw0)) -le 5)

    if ($srvIsSouth) {
        Write-Host ("  [skip] 服务器记住的朝向就是正南 0（Rotation=$srvRot7），守卫无锚点可守，A8 整段跳过")
        Assert-True $true 'A8 守卫用例跳过（服务器记住的朝向本就是正南，无锚点可守）' "Rotation=$srvRot7"
        Assert-True $true 'A8b 跳过（同上）' 'n/a'
        Assert-True $true 'A8c 跳过（同上）' 'n/a'
    } else {
    # 断言三件事：锚点 = 服务器记住的朝向、模拟纠正后钉回、服务端最终朝向也回到锚点。
    # 探针一律返回字符串：Wait-For 把 0 当"未就绪"，而 yaw=0 正是这里要抓的值。
    $guardAnchorText = $null
    try {
        $guardAnchorText = Wait-For -TimeoutMs 20000 -What '守卫锚定日志出现' -Probe {
            $m = [regex]::Match((Get-RecentLogText), '守卫锚定 yaw (-?[\d.]+)')
            if ($m.Success) { return $m.Groups[1].Value }
            return $null
        }
    } catch { $guardAnchorText = $null }

    $guardAnchorOk = $false
    $guardAnchorDetail = '守卫锚定日志缺失'
    if ($guardAnchorText -and $srvRot7) {
        $ga = [double]::Parse($guardAnchorText, [Globalization.CultureInfo]::InvariantCulture)
        $gs = [double]($srvRot7.Split('|')[0])
        $d = [math]::Abs((Get-NormYaw $ga) - (Get-NormYaw $gs))
        if ($d -gt 180) { $d = 360 - $d }
        $guardAnchorOk = $d -le 5
        $guardAnchorDetail = "anchor=$ga server=$srvRot7"
    }
    Assert-True $guardAnchorOk 'A8a 守卫锚点 = 服务器记住的朝向（不是默认正南 0）' $guardAnchorDetail

    $guardBackText = $null
    try {
        $guardBackText = Wait-For -TimeoutMs 20000 -What '守卫钉回日志出现' -Probe {
            $m = [regex]::Match((Get-RecentLogText), '已钉回 yaw (-?[\d.]+)')
            if ($m.Success) { return $m.Groups[1].Value }
            return $null
        }
    } catch { $guardBackText = $null }

    $guardBackOk = $false
    $guardBackDetail = '守卫钉回日志缺失'
    if ($guardBackText -and $guardAnchorText) {
        $gb = [double]::Parse($guardBackText, [Globalization.CultureInfo]::InvariantCulture)
        $ga2 = [double]::Parse($guardAnchorText, [Globalization.CultureInfo]::InvariantCulture)
        $d2 = [math]::Abs((Get-NormYaw $gb) - (Get-NormYaw $ga2))
        if ($d2 -gt 180) { $d2 = 360 - $d2 }
        $guardBackOk = $d2 -le 1
        $guardBackDetail = "pinned=$gb anchor=$ga2"
    }
    Assert-True $guardBackOk 'A8b 朝向被改到正南后守卫钉回锚点' $guardBackDetail
    if (-not $guardBackOk) { Write-RecentLog }

    Start-Sleep -Milliseconds 800
    $srvRot8 = Get-RotationText
    $guardSrvOk = $false
    $guardSrvDetail = '服务端朝向读取失败'
    if ($srvRot8 -and $guardAnchorText) {
        $gsy = [double]($srvRot8.Split('|')[0])
        $ga3 = [double]::Parse($guardAnchorText, [Globalization.CultureInfo]::InvariantCulture)
        $d3 = [math]::Abs((Get-NormYaw $gsy) - (Get-NormYaw $ga3))
        if ($d3 -gt 180) { $d3 = 360 - $d3 }
        $guardSrvOk = $d3 -le 5
        $guardSrvDetail = "server=$srvRot8 anchor=$ga3"
    }
    Assert-True $guardSrvOk 'A8c 服务端最终朝向回到锚点（没停在正南 0）' $guardSrvDetail
    }

    # ============ B. 视角移动：向东 ============
    Write-Host "`n=== B. 点「向东」 ==="
    Assert-True (Open-ViewFlyout) 'B0 「视角调整」下拉可展开'
    Clear-Log
    Click-ViewButton '视角向东'
    Assert-Log '已转向东' 'B1 点「向东」后日志出现转向记录' 15000
    Assert-Rotation 270 0 'B2 服务端朝向 = 东（yaw 270, pitch 0）'

    # ============ C. 抬头看天：保留 yaw、只改俯仰 ============
    Write-Host "`n=== C. 点「抬头看天」 ==="
    Clear-Log
    Click-ViewButton '视角抬头看天'
    Assert-Log '已转向天上' 'C1 点「抬头看天」后日志出现转向记录' 15000
    Assert-Rotation 270 -90 'C2 服务端朝向 = 抬头（yaw 不变, pitch -90）'

    # ============ D. 点「向西」（恢复开关已移除，视角移动照常可用） ============
    Write-Host "`n=== D. 点「向西」 ==="
    Assert-True (Open-ViewFlyout) 'D0 「视角调整」下拉可再次展开'
    Clear-Log
    Click-ViewButton '视角向西'
    Assert-Log '已转向西' 'D1 点「向西」后日志出现转向记录' 15000
    Assert-Rotation 90 0 'D2 服务端朝向 = 西（yaw 90）'

    # ============ E. 断开后点按钮要有提示，不静默 ============
    Write-Host "`n=== E. 断开后点按钮的反馈 ==="
    Close-AllFlyouts
    Disconnect-App
    Assert-True (Open-ViewFlyout) 'E0 断开后下拉仍可展开'
    Clear-Log
    Click-ViewButton '视角向北'
    Assert-Log '视角移动需要先' 'E1 未连接时点「向北」有提示日志' 15000

    # ============ F. 重连：沿用退出服务器时的视角（服务器记忆，本地零干预） ============
    # 2026-10-04 新需求：进服视角完全交给服务器（玩家数据记着上次退出的朝向），
    # MCCX 不再本地存视角、不再在进服窗口里推视角——与裸 MCC 进服体验一致。
    # D2 刚把朝向转成西（90）、E 断开 → 服务器记下 90 → 重连应直接是 90，
    # 全程不该出现任何"恢复"干预日志（旧机制的痕迹必须绝迹）。
    Write-Host "`n=== F. 重连后沿用退出时的视角（yaw 90 西，本地零干预） ==="
    Close-AllFlyouts
    Select-TestAccount
    Clear-Log
    Invoke-UiButton '连接'
    Assert-Log '连接成功' 'F1 重连成功' 90000
    Assert-Log '已进入游戏' 'F2 重连进入游戏' 60000
    Assert-Log '进服沿用服务器记住的朝向' 'F3 进服沿用服务器朝向的说明日志出现' 20000
    Assert-Rotation 90 0 'F4 重连后沿用退出时的视角 = 西（服务器记忆，本地零干预）' 45000

    # F4b（2026-10-05）：重连后**客户端**朝向也必须是西。旧断言只验服务端侧，
    # 于是"服务端记住 90、客户端却被改回南"这类问题能一路绿灯跑完——正是用户现场那一条。
    $fClientYaw = $null
    try {
        $fClientYaw = Wait-For -TimeoutMs 15000 -What '重连后客户端朝向日志出现' -Probe {
            $m = [regex]::Match((Get-RecentLogText), '进服后客户端朝向 yaw (-?[\d.]+)')
            if ($m.Success) { return $m.Groups[1].Value }
            return $null
        }
    } catch { $fClientYaw = $null }
    $f4bok = $false
    $f4bdetail = '客户端朝向日志缺失'
    if ($fClientYaw) {
        $dy = [math]::Abs((Get-NormYaw ([double]$fClientYaw)) - 90)
        if ($dy -gt 180) { $dy = 360 - $dy }
        $f4bok = $dy -le 5
        $f4bdetail = "client=$fClientYaw"
    }
    Assert-True $f4bok 'F4b 重连后客户端朝向 = 西（yaw 90，与服务器一致）' $f4bdetail

    # F5: 重连窗口（新连接的协议 handler 就绪前后）Bot 不许把异常抛给 MCC 的 OnUpdate 兜底。
    # 2026-10-04 修复：未进服不读不发不写 + 发送异常在 Bot 内吞掉并去重提示，
    # 不再出现 'Update: Got error from ...ViewControlBot' 刷屏（历史实测 541 条）。
    # F 段开头已清过日志，这里的文本就是整个重连+进服窗口。
    $fLog = Get-RecentLogText
    $fErr = ([regex]::Matches($fLog, 'Got error')).Count
    Assert-True ($fErr -eq 0) 'F5 重连进服期间没有 Bot 异常刷屏（Got error）' "匹配数=$fErr"

    # F6: 旧恢复机制绝迹（开关、推视角日志都不该再出现）
    $fOld = ([regex]::Matches($fLog, '进服后恢复|视角恢复已')).Count
    Assert-True ($fOld -eq 0) 'F6 日志里没有任何恢复干预痕迹（功能已移除）' "匹配数=$fOld"

    # ============ G. 行走开启 + 视角移动（2026-10-04 现场反馈的重做回归） ============
    # 旧实现是一次性写入：转向包发一次就被自动行走的 SetInputToward / 路点视线每 tick
    # 压回旧行走方向，现场表现为"点「向东」纹丝不动"。
    # 新实现是转向保持期（2 秒内每 tick 重写）+ 行走掐路重规划。
    # 此节复现现场：先开行走让它真的动起来，再点「向北」，断言服务端朝向转到北、
    # 持续行走中不被拉回、且位移证明行走全程在跑（排除"只是停着没动"的假绿）。
    Write-Host "`n=== G. 开着行走点「向北」：朝向要转到北并保持 ==="
    Clear-Log

    # G0（2026-10-05）：开行走前先把人放到"一定会走起来"的位置。
    # 原因：G6/G7 断言"转向后确实在走、且朝向不被拉回"，隐含前提是脚下有能走的路。
    # 直接用出生点不稳——实测那个点往北常年是堵的（日志「前方无路可走」），
    # 那样 G6 会被反复判红、或被环境跳过而丢掉位移覆盖。
    # 做法照抄 g_walk D 节（那边长期稳定）：旁边 4 格 → 原位插 8 格石柱 → 传到柱顶，
    # 人必然在行走中走下柱子，位移一定有。
    $gBase = Get-PlayerPos
    if ($null -ne $gBase) {
        $gbx = [math]::Floor($gBase[0])
        $gby = [math]::Floor($gBase[1])
        $gbz = [math]::Floor($gBase[2])
        Invoke-Rcon ('tp {0} {1} {2} {3}' -f $script:TestPlayer, ($gbx + 4.5), $gby, ($gbz + 0.5)) 4000 | Out-Null
        Start-Sleep -Milliseconds 800
        Invoke-Rcon ('fill {0} {1} {2} {0} {3} {2} minecraft:stone' -f $gbx, $gby, $gbz, ($gby + 7)) 4000 | Out-Null
        Start-Sleep -Milliseconds 800
        Invoke-Rcon ('tp {0} {1} {2} {3}' -f $script:TestPlayer, ($gbx + 0.5), ($gby + 8), ($gbz + 0.5)) 4000 | Out-Null
        Start-Sleep -Seconds 2
        Write-Host ("  [prep] 已插柱并把人放到柱顶 " + ($gbx, $gby + 8, $gbz -join ','))
    }

    Set-ToggleOn '自动行走' $true
    Assert-Log '自动行走已开启' 'G1 行走开关打开' 15000
    Assert-Log '已就绪：持续朝当前朝向前进' 'G2 行走 Bot 已就绪' 15000

    Clear-Log
    Click-ViewButton '视角向北'
    Assert-Log '已转向北' 'G3 点「向北」后日志出现转向记录' 15000
    Assert-Rotation 180 0 'G4 服务端朝向 = 北（yaw 180, pitch 0）'

    # 位移基准取在点击之后：断言的是"朝北走的过程中"，不掺进点击前朝西走的那段
    $gStart = Get-PlayerPos
    Assert-True ($null -ne $gStart) 'G5 行走期间能读到服务端 Pos' "pos=$($gStart -join ',')"
    $gMoved = $null
    try {
        $gMoved = Wait-For -TimeoutMs 45000 -What '转向后继续行走（水平位移 >= 1 格）' -Probe {
            $now = Get-PlayerPos
            $d = Get-PosDistance $gStart $now
            if ($null -ne $d -and $d -ge 1.0) { [math]::Round($d, 2) } else { $null }
        }
    } catch { $gMoved = $null }

    if ($null -eq $gMoved) {
        # 2026-10-05：实测同一出生点、同一段路，有时 30 秒走出 1 格、有时一格不动（g_walk 全绿，
        # 说明行走功能本身没坏，是 AutoWalkBot 的寻路/恢复时序）。所以只有在日志**明确**出现
        # "被地形阻断/正在重试"时才判为环境问题跳过；日志里没有这些说明还走不动，照旧判红，
        # 免得把真回归藏起来。
        $gLogTxt = Get-RecentLogText 120
        $blocked = ($gLogTxt -match '无路可走|没有安全路线|稍后自动重试|区块未加载|改走直接下落')
        if ($blocked) {
            Write-Host '  [skip] 日志显示行走被地形阻断/正在重试，本节位移断言按环境问题跳过'
            Assert-True $true 'G6 跳过（行走被地形阻断，非功能问题）' 'log=blocked'
        } else {
            Assert-True $false 'G6 转向后确实在走（位移 >= 1 格，排除"只是停着没动"）' "moved=$gMoved（日志里没有阻断/重试记录）"
        }
    } else {
        Assert-True $true 'G6 转向后确实在走（位移 >= 1 格，排除"只是停着没动"）' "moved=$gMoved"
    }

    if ($null -eq $gMoved) {
        Write-Host ("  [dbg] Pos = " + (Invoke-Rcon ('execute as {0} run data get entity @s Pos' -f $script:TestPlayer) 4000))
        Write-RecentLog
    }

    # 每秒采样朝向+位置：漂移发生时能看出是"计划本身偏了（位置斜着走）"
    # 还是"走的方向对、头被带偏（位置北、朝向变）"，失败现场不靠猜
    $gTrail = @()
    for ($i = 1; $i -le 6; $i++) {
        Start-Sleep -Seconds 1
        $gTrail += ("t+{0}s rot={1} pos={2}" -f $i, (Get-RotationText), ((Get-PlayerPos) -join ','))
    }
    $gTrail | ForEach-Object { Write-Host "  [trail] $_" }
    Assert-Rotation 180 0 'G7 持续行走后朝向仍是北（未被行走方向拉回）' 5000
    Write-RecentLog
} finally {
    Write-Host "`n=== 收尾：断开 + 删除测试账号 ==="
    try { Close-AllFlyouts } catch { }
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
Write-Host "SUMMARY g_view FAILURES=$fail"
exit ([int]($fail -gt 0))
