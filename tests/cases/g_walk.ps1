# 自动行走 e2e（2026-10-04 需求：只管一直朝前走）：
#   连本地测试服 → 打开「自动行走」→ RCON 读服务端 Pos 核对位移：
#   A（开关关）：默认关、连接进服正常；
#   B（开关开）：日志有开启行 + Bot 就绪行，且服务端看到玩家真的朝前走了（水平位移 >= 1 格）；
#   C（关掉）：日志有关闭行，观察窗内不再产生位移（OnUnload 会取消在途寻路，不是"走到下一个路点才停"）；
#   D（高空方块，2026-10-04 用户反馈回归）：原地拔 8 格石柱、tp 到柱顶再开开关——
#     必须走下来（目标 Y 吸附 + 无安全路线时直接下落），不能站着不动。
# 全程专用测试账号 TestWalk，结束删除，不碰用户已有账号；只用 UIA + RCON，不抢前台焦点。
# 用法：pwsh -ExecutionPolicy Bypass -File tests\cases\g_walk.ps1
$ErrorActionPreference = 'Stop'
. "$(Join-Path $PSScriptRoot '..\lib')\autolib.ps1"

$script:TestUser = 'TestWalk'
$script:TestPort = '25599'
# autolib 默认测试玩家是 MccXBot，本脚本账号叫 TestWalk；不改的话
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

# ---------- RCON：读服务端 Pos（位移断言的唯一依据） ----------
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
    try {
        $txt = Get-RecentLogText
        if ($txt) {
            $lines = @($txt -split "`r?`n")
            $tail = ($lines | Select-Object -Last 30) -join ' | '
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

    Assert-True (-not (Get-ToggleOn '自动行走')) 'A4 「自动行走」默认关' "on=$(Get-ToggleOn '自动行走')"

    $start = Get-PlayerPos
    Assert-True ($null -ne $start) 'A5 服务端能读到玩家 Pos（后续位移断言的基准）' 'pos=parse failed'
    if ($null -eq $start) { Write-Host ("  [dbg] Pos raw: " + (Invoke-Rcon ('execute as {0} run data get entity @s Pos' -f $script:TestPlayer) 4000)) }
    Write-Host ("  [pos] start = " + ($start -join ', '))

    # ============ B. 打开开关：真的朝前走了 ============
    Write-Host "`n=== B. 打开开关：位移 >= 1 格 ==="
    Clear-Log
    Set-ToggleOn '自动行走' $true
    Assert-Log '自动行走已开启' 'B1 开关打开并推送配置' 15000
    # 排查点：必须能区分「Bot 没挂上」和「挂上了但没走」——B1b 失败 = 挂载问题
    Assert-Log '已就绪：持续朝当前朝向前进' 'B1b 自动行走 Bot 已就绪' 15000

    $moved = $null
    try {
        $moved = Wait-For -TimeoutMs 40000 -What '自动行走产生位移（>=1 格）' -Probe {
            $now = Get-PlayerPos
            $d = Get-PosDistance $start $now
            if ($null -ne $d -and $d -ge 1.0) { [math]::Round($d, 2) } else { $null }
        }
    } catch { $moved = $null }
    Assert-True ($null -ne $moved) 'B2 服务端看到玩家朝前走了（水平位移 >= 1 格）' "moved=$moved"
    if ($null -ne $moved) {
        Write-Host ("  [pos] moved = $moved blocks")
    } else {
        Write-RecentLog
        $now = Get-PlayerPos
        Write-Host ("  [pos] end = " + ($now -join ', '))
    }

    # ============ C. 关掉开关：立刻停（含取消在途寻路） ============
    Write-Host "`n=== C. 关掉开关：位移停住 ==="
    Clear-Log
    Set-ToggleOn '自动行走' $false
    Assert-Log '自动行走已关闭' 'C1 开关关闭并推送配置' 15000

    Start-Sleep -Seconds 4          # 卸载 + 在途路点残余的宽限（OnUnload 取消寻路后 1 秒内就该停）
    $p1 = Get-PlayerPos
    Start-Sleep -Seconds 3          # 明确的"继续观察"窗
    $p2 = Get-PlayerPos
    $drift = Get-PosDistance $p1 $p2
    Assert-True ($null -ne $drift -and $drift -lt 0.5) 'C2 关掉后不再产生位移（不是走到下个路点才停）' "drift=$drift p1=$($p1 -join ',') p2=$($p2 -join ',')"
    if ($null -eq $drift) { Write-RecentLog }

    # ============ D. 高空方块：必须走下来（2026-10-04 用户反馈回归） ============
    # 复现现场：原地拔 8 格 1x1 石柱、tp 到柱顶再开开关。老逻辑必然零移动——
    # 目标点 Y 悬在半空 + MCC A* 落差 >3 格不肯走；修复 = 目标 Y 吸附 + 无安全路线时直接下落。
    Write-Host "`n=== D. 高空方块：下落继续行走 ==="
    $base = Get-PlayerPos
    Assert-True ($null -ne $base) 'D0 能读到 Pos（插柱基准）' 'pos=parse failed'

    if ($null -ne $base) {
        $bx = [math]::Floor($base[0])
        $by = [math]::Floor($base[1])
        $bz = [math]::Floor($base[2])

        # 装配顺序防时序坑（2026-10-04 实测教训）：不能"先 tp 上天再补柱"——
        # 8 格自由落体只要 ~0.7 秒，800ms 空隙里人就落回地面了，fill 随后把人埋在原地，
        # D0.5 直接读出地面高度、后面全是在平地走路（假通过/假失败）。
        # 正确顺序：① 人先挪开旁边 4 格（别站在要填的柱子里）→ ② 原位填柱 → ③ 再传到柱顶。
        Invoke-Rcon ('tp {0} {1} {2} {3}' -f $script:TestPlayer, ($bx + 4.5), $by, ($bz + 0.5)) 4000 | Out-Null
        Start-Sleep -Milliseconds 800
        Invoke-Rcon ('fill {0} {1} {2} {0} {3} {2} minecraft:stone' -f $bx, $by, $bz, ($by + 7)) 4000 | Out-Null
        Start-Sleep -Milliseconds 800
        Invoke-Rcon ('tp {0} {1} {2} {3}' -f $script:TestPlayer, ($bx + 0.5), ($by + 8), ($bz + 0.5)) 4000 | Out-Null
        Start-Sleep -Seconds 2

        # D0.5：证明人真的在柱顶（Y >= by+6 且水平贴着柱心）——
        # 没站上去的话 D2 就是在平地走路（假通过），D3 也必假失败。
        $top = Get-PlayerPos
        $topY = if ($null -ne $top) { $top[1] } else { $null }
        $dx = if ($null -ne $top) { [math]::Abs($top[0] - ($bx + 0.5)) } else { 999 }
        $dz = if ($null -ne $top) { [math]::Abs($top[2] - ($bz + 0.5)) } else { 999 }
        $onTop = ($null -ne $topY -and $topY -ge ($by + 6) -and $dx -lt 1.5 -and $dz -lt 1.5)
        Assert-True $onTop 'D0.5 已站到柱顶（插柱 + tp 时序生效）' "y=$topY dx=$dx dz=$dz expect y>=$($by + 6)"

        Clear-Log
        Set-ToggleOn '自动行走' $true
        Assert-Log '自动行走已开启' 'D1 柱顶打开开关' 15000

        $got = $null
        try {
            $got = Wait-For -TimeoutMs 30000 -What '柱顶走下来（水平位移 >= 1 格）' -Probe {
                $now = Get-PlayerPos
                $d = Get-PosDistance $base $now
                if ($null -ne $d -and $d -ge 1.0) { [math]::Round($d, 2) } else { $null }
            }
        } catch { $got = $null }
        Assert-True ($null -ne $got) 'D2 高空方块也能走（下落继续前进，不再站着不动）' "moved=$got"
        if ($null -ne $got) {
            Write-Host ("  [pos] pillar descend & walk = $got blocks")
        } else {
            Write-RecentLog
            $now = Get-PlayerPos
            Write-Host ("  [pos] end = " + ($now -join ', '))
        }

        # D2x：8 格下落（伤害 5）不该摔死——出现死亡/重生说明位移是重生传送凑的（假通过）
        $dlog = Get-RecentLogText
        Assert-True ($dlog -notmatch 'You are dead|fell from a high place') 'D2x 下落过程没有摔死/重生（位移真来自行走）' 'log contains death/respawn'
        if ($dlog -match 'You are dead|fell from a high place') { Write-RecentLog }

        # 走下来必然经过"安全路线不存在 → 直接下落"分支（柱高 8 > MCC 的 3 格安全落差）。
        # 失败时打印最近日志：区分"提示被别的 ComplainOnce 抢了"还是"根本没走下落分支"。
        $d3ok = $false
        for ($i = 0; $i -lt 20 -and -not $d3ok; $i++) {
            if ((Get-RecentLogText) -match '改走直接下落') { $d3ok = $true }
            else { Start-Sleep -Milliseconds 500 }
        }
        if (-not $d3ok) { Write-RecentLog }
        Assert-True $d3ok 'D3 触发了无安全路线时的下落分支' 'missing: 改走直接下落（见上方日志）'

        # 拆柱（玩家已落地；只清掉自己填的 by..by+7，原地面不碰）
        Invoke-Rcon ('fill {0} {1} {2} {0} {3} {2} air' -f $bx, $by, $bz, ($by + 7)) 4000 | Out-Null
        Set-ToggleOn '自动行走' $false
        Assert-Log '自动行走已关闭' 'D4 柱顶测试后关掉开关' 15000
    }
} finally {
    Write-Host "`n=== 收尾：关自动行走 + 断开 + 删除测试账号 ==="
    try { Set-ToggleOn '自动行走' $false } catch { }
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
Write-Host "SUMMARY g_walk FAILURES=$fail"
exit ([int]($fail -gt 0))
